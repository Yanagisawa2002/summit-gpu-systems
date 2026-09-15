// Whole-task driver for the pinned public ArborX molecular dynamics example.
// The force/update implementation is extracted verbatim into UpstreamStep.hpp.
#include "UpstreamStep.hpp"
#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>
#if defined(SUMMIT_MD_CUDA)
#include <cuda_runtime_api.h>
#include <unistd.h>
#endif
#if defined(__linux__)
#include <time.h>
#endif

using Clock=std::chrono::steady_clock;
double ms(Clock::time_point a,Clock::time_point b) { return std::chrono::duration<double,std::milli>(b-a).count(); }
std::int64_t ns(Clock::time_point t) { return std::chrono::duration_cast<std::chrono::nanoseconds>(t.time_since_epoch()).count(); }
template<class T> void put(std::ostream& o,T const& v) { o.write(reinterpret_cast<char const*>(&v),sizeof(T)); if(!o)throw std::runtime_error("write failed"); }
template<class T> T get(std::istream& i) { T v; i.read(reinterpret_cast<char*>(&v),sizeof(T)); if(!i)throw std::runtime_error("truncated input"); return v; }
using Ints=Kokkos::View<int*,MemorySpace>;
struct Csr { Ints ids,offsets; };
using HostInts=Ints::HostMirror;
struct HostCsr { HostInts ids,offsets; };
HostCsr host_csr(Csr const&c) {
  return {Kokkos::create_mirror_view_and_copy(Kokkos::HostSpace{},c.ids),
          Kokkos::create_mirror_view_and_copy(Kokkos::HostSpace{},c.offsets)};
}
struct Raw { std::vector<std::array<float,3>> p,v; float radius=3; bool membershipOnly=false; };
struct State { std::vector<float> p,v,f; };
#include "TiledAllPairs.hpp"

#if defined(SUMMIT_MD_CUDA)
std::string actual_cuda_uuid(){
  int device=0;cudaDeviceProp properties{};
  if(cudaGetDevice(&device)!=cudaSuccess||cudaGetDeviceProperties(&properties,device)!=cudaSuccess)
    throw std::runtime_error("Actual CUDA device identity unavailable");
  auto const&uuid=properties.uuid;
  std::ostringstream value;value<<"GPU-"<<std::hex<<std::setfill('0');
  for(int i=0;i<16;++i){if(i==4||i==6||i==8||i==10)value<<'-';value<<std::setw(2)<<unsigned(static_cast<unsigned char>(uuid.bytes[i]));}
  return value.str();
}
#endif

bool exact(ArborX::Point<3> a,ArborX::Point<3> b,float radius) {
  float x=a[0]-b[0],y=a[1]-b[1],z=a[2]-b[2];
  float xx=x*x,yy=y*y,zz=z*z,sum=xx; sum+=yy; sum+=zz;
  return std::sqrt(sum)<=radius;
}
Csr arborx_query(Points p,float radius) {
  ExecutionSpace exec;
  ArborX::BoundingVolumeHierarchy tree(exec,ArborX::Experimental::attach_indices(p));
  Csr c{Ints("ids",0),Ints("offsets",0)};
  tree.query(exec,ArborX::Experimental::attach_indices<int>(Neighbors<MemorySpace>{p,radius}),ExcludeSelfCollision{},c.ids,c.offsets);
  exec.fence(); return c;
}
#if !defined(SUMMIT_MD_CUDA)
Csr grid_query(Points p,float radius) {
  int n=p.extent_int(0); float width=radius*(1+8*std::numeric_limits<float>::epsilon());
  if(!(width>0))throw std::runtime_error("grid requires positive radius");
  std::array<double,3> lo{},hi{}; for(int d=0;d<3;++d)lo[d]=hi[d]=p(0)[d];
  for(int i=0;i<n;++i)for(int d=0;d<3;++d){lo[d]=std::min(lo[d],double(p(i)[d]));hi[d]=std::max(hi[d],double(p(i)[d]));}
  std::array<int,3> shape{}; std::int64_t nb=1;
  for(int d=0;d<3;++d){shape[d]=int(std::floor((hi[d]-lo[d])/width))+1;nb*=shape[d];}
  if(nb>16*1024*1024)throw std::runtime_error("conventional grid exceeds explicit 16M-cell budget");
  auto cell=[&](double x,int d){return std::clamp(int(std::floor((x-lo[d])/width)),0,shape[d]-1);};
  auto key=[&](int x,int y,int z){return (z*shape[1]+y)*shape[0]+x;};
  std::vector<int> count(nb,0),offset(nb+1,0),cursor,slots(n);
  for(int i=0;i<n;++i)++count[key(cell(p(i)[0],0),cell(p(i)[1],1),cell(p(i)[2],2))];
  for(int i=0;i<nb;++i)offset[i+1]=offset[i]+count[i];cursor=offset;
  for(int i=0;i<n;++i)slots[cursor[key(cell(p(i)[0],0),cell(p(i)[1],1),cell(p(i)[2],2))]++]=i;
  Csr c{Ints("ids",0),Ints("offsets",n+1)};
  auto visit=[&](int q,auto emit){
    std::array<int,3> a{},b{};
    for(int d=0;d<3;++d){a[d]=cell(double(p(q)[d])-width,d);b[d]=cell(double(p(q)[d])+width,d);}
    for(int z=a[2];z<=b[2];++z)for(int y=a[1];y<=b[1];++y)for(int x=a[0];x<=b[0];++x){
      int k=key(x,y,z);for(int at=offset[k];at<offset[k+1];++at){int j=slots[at];if(j!=q&&exact(p(q),p(j),radius))emit(j);}
    }
  };
  // CPU grid construction/prefix are serial; independent query rows use the
  // explicitly selected Serial/OpenMP space. No GPU-labelled host fallback.
  ExecutionSpace exec;
  Kokkos::parallel_for("CPU grid counts",Kokkos::RangePolicy(exec,0,n),[&](int q){
    int rowCount=0;visit(q,[&](int){++rowCount;});c.offsets(q+1)=rowCount;
  });exec.fence();
  for(int q=0;q<n;++q)c.offsets(q+1)+=c.offsets(q);
  c.ids=Ints(Kokkos::view_alloc(Kokkos::WithoutInitializing,"ids"),c.offsets(n));
  Kokkos::parallel_for("CPU grid IDs",Kokkos::RangePolicy(exec,0,n),[&](int q){
    int at=c.offsets(q);visit(q,[&](int j){c.ids(at++)=j;});
  });exec.fence();
  return c;
}
Csr brute_query(Points p,float radius) {
  int n=p.extent_int(0);std::vector<int> ids;Csr c{Ints("ids",0),Ints("offsets",n+1)};
  for(int q=0;q<n;++q){for(int j=0;j<n;++j)if(j!=q&&exact(p(q),p(j),radius))ids.push_back(j);c.offsets(q+1)=int(ids.size());}
  c.ids=Ints("ids",ids.size());for(int i=0;i<int(ids.size());++i)c.ids(i)=ids[i];return c;
}
#endif
void equal_csr(HostCsr const&a,HostCsr const&b) {
  if(a.offsets.size()!=b.offsets.size()||a.ids.size()!=b.ids.size())throw std::runtime_error("CSR shape mismatch");
  for(int q=0;q<int(a.offsets.size())-1;++q){
    if(a.offsets(q)!=b.offsets(q)||a.offsets(q+1)!=b.offsets(q+1))throw std::runtime_error("CSR row length mismatch at "+std::to_string(q));
    // Avoid forming null-pointer ranges for a zero-member CSR.
    std::vector<int> x,y;
    for(int j=a.offsets(q);j<a.offsets(q+1);++j)x.push_back(a.ids(j));
    for(int j=b.offsets(q);j<b.offsets(q+1);++j)y.push_back(b.ids(j));
    std::sort(x.begin(),x.end());std::sort(y.begin(),y.end());if(x!=y)throw std::runtime_error("CSR membership/multiplicity mismatch at "+std::to_string(q));
  }
}
void write_csr(std::filesystem::path path,HostCsr const&c) {
  std::ofstream o(path,std::ios::binary);o.write("SUMDCSR1",8);put(o,std::uint32_t(c.offsets.size()-1));put(o,std::uint32_t(c.ids.size()));
  for(int i=0;i<int(c.offsets.size());++i)put(o,std::uint32_t(c.offsets(i)));for(int i=0;i<int(c.ids.size());++i)put(o,std::uint32_t(c.ids(i)));
}
HostCsr read_csr(std::filesystem::path path) {
  std::ifstream in(path,std::ios::binary);char magic[8]{};in.read(magic,8);if(!in||std::string(magic,8)!="SUMDCSR1")throw std::runtime_error("CSR ABI");
  auto n=get<std::uint32_t>(in),m=get<std::uint32_t>(in);
  if(n<1||n>1000000||m>16*1024*1024)throw std::runtime_error("reference CSR budget");
  HostCsr c{HostInts("reference ids",m),HostInts("reference offsets",n+1)};
  for(int i=0;i<int(c.offsets.size());++i)c.offsets(i)=get<std::uint32_t>(in);
  for(int i=0;i<int(c.ids.size());++i)c.ids(i)=get<std::uint32_t>(in);
  if(c.offsets(0)!=0||c.offsets(n)!=int(m)||in.peek()!=EOF)throw std::runtime_error("reference CSR structure");
  for(int q=0;q<int(n);++q){
    if(c.offsets(q)<0||c.offsets(q+1)<c.offsets(q)||c.offsets(q+1)>int(m))throw std::runtime_error("reference CSR offset");
    for(int j=c.offsets(q);j<c.offsets(q+1);++j)if(c.ids(j)<0||c.ids(j)>=int(n)||c.ids(j)==q)throw std::runtime_error("reference CSR ID");
  }return c;
}
#if defined(SUMMIT_MD_SERIAL)
void write_input(std::filesystem::path path,Input const&v,bool membershipOnly=false) {
  std::ofstream o(path,std::ios::binary);o.write("SUMD0001",8);put(o,std::uint32_t(v.particles.size()));put(o,3.f);put(o,std::uint32_t(membershipOnly));
  for(int i=0;i<int(v.particles.size());++i){for(int d=0;d<3;++d)put(o,v.particles(i)[d]);put(o,std::uint32_t(i));}
  for(int i=0;i<int(v.particles.size());++i)for(int d=0;d<3;++d)put(o,v.velocities(i,d));
}
#endif
Raw read_input(std::filesystem::path path) {
  std::ifstream in(path,std::ios::binary);char magic[8]{};in.read(magic,8);if(!in||std::string(magic,8)!="SUMD0001")throw std::runtime_error("input ABI");
  auto n=get<std::uint32_t>(in);Raw r;r.radius=get<float>(in);r.membershipOnly=get<std::uint32_t>(in)!=0;
  if(n<1||n>1000000||r.radius!=3.f)throw std::runtime_error("input budget/radius");r.p.resize(n);r.v.resize(n);
  for(std::uint32_t i=0;i<n;++i){for(float&x:r.p[i]){x=get<float>(in);if(!std::isfinite(x))throw std::runtime_error("nonfinite position");}if(get<std::uint32_t>(in)!=i)throw std::runtime_error("source identity changed");}
  for(auto&v:r.v)for(float&x:v){x=get<float>(in);if(!std::isfinite(x))throw std::runtime_error("nonfinite velocity");}if(in.peek()!=EOF)throw std::runtime_error("trailing input");return r;
}
State state(Points deviceP,Vectors deviceV,Vectors deviceF) {
  // Full audit copies occur only after the completed backend-resident task.
  auto p=Kokkos::create_mirror_view_and_copy(Kokkos::HostSpace{},deviceP);
  auto v=Kokkos::create_mirror_view_and_copy(Kokkos::HostSpace{},deviceV);
  auto f=Kokkos::create_mirror_view_and_copy(Kokkos::HostSpace{},deviceF);
  State s;for(int i=0;i<int(p.size());++i)for(int d=0;d<3;++d){s.p.push_back(p(i)[d]);s.v.push_back(v(i,d));s.f.push_back(f(i,d));}return s;
}
void write_state(std::filesystem::path path,State const&s) {
  std::ofstream o(path,std::ios::binary);o.write("SUMDSTA1",8);put(o,std::uint32_t(s.p.size()/3));
  for(auto const&v:{s.p,s.v,s.f})for(float x:v)put(o,x);
}
State read_state(std::filesystem::path path) {
  std::ifstream in(path,std::ios::binary);char magic[8]{};in.read(magic,8);if(!in||std::string(magic,8)!="SUMDSTA1")throw std::runtime_error("state ABI");
  int n=get<std::uint32_t>(in);if(n<1||n>1000000)throw std::runtime_error("reference state budget");
  State s;for(auto v:{&s.p,&s.v,&s.f}){v->resize(n*3);for(float&x:*v){x=get<float>(in);if(!std::isfinite(x))throw std::runtime_error("reference state nonfinite");}}
  if(in.peek()!=EOF)throw std::runtime_error("trailing state");return s;
}
std::array<double,3> equal_state(State const&a,State const&b) {
  std::array<double,3> maxima{};int field=0;
  for(auto pair:{std::pair{&a.p,&b.p},std::pair{&a.v,&b.v},std::pair{&a.f,&b.f}}){
    auto const&x=*pair.first;auto const&y=*pair.second;if(x.size()!=y.size())throw std::runtime_error("state shape");
    double absTol=field==2?0.002:0.00002,relTol=field==0?0.000002:0.00002;
    for(int i=0;i<int(x.size());++i){double err=std::abs(double(x[i])-y[i]);maxima[field]=std::max(maxima[field],err);
      if(!std::isfinite(x[i])||!std::isfinite(y[i])||err>absTol+relTol*std::abs(y[i]))throw std::runtime_error("state precision mismatch field="+std::to_string(field)+" element="+std::to_string(i));}
    ++field;
  }return maxima;
}
#if defined(SUMMIT_MD_SERIAL)
void export_case(std::filesystem::path dir,std::string name,int cells,bool perturbed=false) {
  auto input=initialize(cells,1.7f);
  if(perturbed)for(int i=0;i<int(input.particles.size());++i)for(int d=0;d<3;++d)input.particles(i)[d]+=.15f*std::sin(float(i*3+d)*.23f);
  write_input(dir/(name+".bin"),input);auto csr=arborx_query(input.particles,3);equal_csr(host_csr(csr),host_csr(grid_query(input.particles,3)));
  if(input.particles.size()<=4000)equal_csr(host_csr(csr),host_csr(brute_query(input.particles,3)));
  write_csr(dir/(name+".csr"),host_csr(csr));auto forces=consume(input.particles,input.velocities,csr.ids,csr.offsets);
  write_state(dir/(name+".state"),state(input.particles,input.velocities,forces));
  std::cout<<name<<" n="<<input.particles.size()<<" complete IDs="<<csr.ids.size()<<"\n";
}
void export_boundary(std::filesystem::path dir) {
  Input input{Points("boundary",8),Vectors("velocity",8)};
  std::array<std::array<float,3>,8> p={{{0,0,0},{3,0,0},{std::nextafter(3.f,0.f),0,0},{std::nextafter(3.f,4.f),0,0},{0,0,0},{-3,0,0},{0,3,0},{0,0,3}}};
  for(int i=0;i<8;++i)for(int d=0;d<3;++d)input.particles(i)[d]=p[i][d];
  write_input(dir/"boundary-membership.bin",input,true);auto c=arborx_query(input.particles,3);equal_csr(host_csr(c),host_csr(brute_query(input.particles,3)));equal_csr(host_csr(c),host_csr(grid_query(input.particles,3)));write_csr(dir/"boundary-membership.csr",host_csr(c));
}
void export_functional(std::filesystem::path dir,bool isolated) {
  int n=isolated?3:129;std::string name=isolated?"isolated-3":"tail-129";
  Input input{Points("functional points",n),Vectors("functional velocities",n)};
  for(int i=0;i<n;++i){
    input.particles(i)=isolated?ArborX::Point<3>{float(i-1)*10.f,0.f,0.f}:
                                ArborX::Point<3>{float(i)*.7f,float(i%3)*.13f,float(i%5)*.21f};
    for(int d=0;d<3;++d)input.velocities(i,d)=float((i+d)%7)*.01f;
  }
  write_input(dir/(name+".bin"),input);auto csr=arborx_query(input.particles,3);
  equal_csr(host_csr(csr),host_csr(brute_query(input.particles,3)));
  equal_csr(host_csr(csr),host_csr(grid_query(input.particles,3)));
  if(isolated&&csr.ids.size()!=0)throw std::runtime_error("Isolated fixture must have zero neighbours");
  write_csr(dir/(name+".csr"),host_csr(csr));auto forces=consume(input.particles,input.velocities,csr.ids,csr.offsets);
  write_state(dir/(name+".state"),state(input.particles,input.velocities,forces));
}
#endif
int main(int argc,char**argv) {
  Kokkos::ScopeGuard kokkos(argc,argv);
  try {
    if(argc==3&&std::string(argv[1])=="export") {
#if defined(SUMMIT_MD_SERIAL)
      std::filesystem::path dir=argv[2];if(std::filesystem::exists(dir))throw std::runtime_error("keep prior export; new directory required");std::filesystem::create_directories(dir);
      export_case(dir,"discovery-8",8);export_case(dir,"small-6",6);export_case(dir,"original-10",10);export_case(dir,"large-20",20);export_case(dir,"perturbed-10",10,true);export_boundary(dir);
      export_functional(dir,true);export_functional(dir,false);return 0;
#else
      throw std::runtime_error("Canonical snapshots must be exported by the Serial binary once");
#endif
    }
#if defined(SUMMIT_MD_CUDA)
    if(argc==7&&std::string(argv[1])=="audit-overflow"){
      std::filesystem::path inputPath=argv[2],output=argv[3];int tile=std::stoi(argv[4]),cap=std::stoi(argv[5]);
      if(std::string(argv[6])!="expect-rejection"||std::filesystem::exists(output))throw std::runtime_error("New explicit overflow-check output required");
      auto raw=read_input(inputPath);auto golden=read_csr(inputPath.replace_extension(".csr"));
      if(cap<0||cap>=int(golden.ids.size()))throw std::runtime_error("Overflow fixture must exceed its declared cap");
      std::filesystem::create_directories(output);
      Points points("overflow input",raw.p.size());auto host=Kokkos::create_mirror_view(points);
      for(int i=0;i<int(raw.p.size());++i)for(int d=0;d<3;++d)host(i)[d]=raw.p[i][d];Kokkos::deep_copy(points,host);
      try{ordinary_gpu::audit(points,raw.radius,tile,cap);}catch(ordinary_gpu::AuditCapacityError const&e){
        if(e.required!=int(golden.ids.size()))throw std::runtime_error("Overflow-check required membership count differs from golden");
        std::ofstream report(output/"overflow-check.json");
        report<<"{\"expectedCapacityRejection\":true,\"maxIds\":"<<cap<<",\"goldenIds\":"<<golden.ids.size()
              <<",\"tile\":"<<tile<<",\"processPid\":"<<getpid()<<",\"gpuUuid\":\""<<actual_cuda_uuid()<<"\"}\n";
        if(!report)throw std::runtime_error("Overflow acceptance report write failed");
        std::cout<<e.what()<<'\n';return 0;
      }
      throw std::runtime_error("Expected capacity rejection did not occur");
    }
#endif
    if(argc!=7)throw std::runtime_error("export directory | run arborx|grid|tiled128|tiled256 input.bin new-output-directory warmups measured");
    std::string arm=argv[2];bool tiled=arm=="tiled128"||arm=="tiled256";int tile=arm=="tiled128"?128:256;
    if(std::string(argv[1])!="run"||(arm!="arborx"&&arm!="grid"&&!tiled))throw std::runtime_error("unknown arm");
#if defined(SUMMIT_MD_CUDA)
    if(arm=="grid")throw std::runtime_error("CPU grid is not a CUDA arm");
#else
    if(tiled)throw std::runtime_error("Tiled GPU control requires an explicit CUDA build");
#endif
    std::filesystem::path path=argv[3],out=argv[4];if(std::filesystem::exists(out))throw std::runtime_error("keep prior run; new output required");std::filesystem::create_directories(out);
    auto raw=read_input(path);auto expectedCsr=read_csr(path.replace_extension(".csr"));State expected;
    if(!raw.membershipOnly)expected=read_state(path.replace_extension(".state"));
    int n=int(raw.p.size()),warm=std::stoi(argv[5]),reps=std::stoi(argv[6]);
    if(warm<0||reps<1||warm>10000||reps>100000)throw std::runtime_error("repetition budget");
    {std::ofstream config(out/"execution-space.txt");
      config<<"selectedExecutionSpace="<<ExecutionSpace::name()<<"\nreportedConcurrency="<<ExecutionSpace{}.concurrency()<<'\n';
#if defined(SUMMIT_MD_CUDA)
      config<<"processPid="<<getpid()<<"\nselectedGpuUuid="<<actual_cuda_uuid()<<"\n";
#endif
#if defined(__linux__)
      auto before=Clock::now();timespec monotonic{};if(clock_gettime(CLOCK_MONOTONIC,&monotonic)!=0)throw std::runtime_error("monotonic calibration clock unavailable");auto after=Clock::now();
      config<<"steadyBeforeNs="<<ns(before)<<"\nmonotonicNs="<<(std::int64_t(monotonic.tv_sec)*1000000000+monotonic.tv_nsec)<<"\nsteadyAfterNs="<<ns(after)<<"\n";
#endif
      Kokkos::print_configuration(config,true);}
    std::ofstream csv(out/"timings.csv");csv<<std::setprecision(17)<<"step,phase,allocationMs,firstUseMs,copyMs,queryMs,consumeMs,hostWallMs,auditMs,taskStartNs,taskEndNs,ids,positionMaxAbs,velocityMaxAbs,forceMaxAbs,verified\n";
    auto allocated=Clock::now();
    Points points("working points",n);Vectors velocity("working velocity",n);
    // Mirrors preserve the device layout. CPU mirrors alias working storage,
    // so CPU arms do not pay for a redundant staging allocation/copy.
    auto packedPoints=Kokkos::create_mirror_view(Kokkos::HostSpace{},points);
    auto packedVelocity=Kokkos::create_mirror_view(Kokkos::HostSpace{},velocity);
    ExecutionSpace exec;exec.fence();double allocationMs=ms(allocated,Clock::now());
    for(int step=-warm;step<reps;++step){
      auto a=Clock::now();for(int i=0;i<n;++i)for(int d=0;d<3;++d){packedPoints(i)[d]=raw.p[i][d];packedVelocity(i,d)=raw.v[i][d];}
      if(points.data()!=packedPoints.data())Kokkos::deep_copy(exec,points,packedPoints);
      if(velocity.data()!=packedVelocity.data())Kokkos::deep_copy(exec,velocity,packedVelocity);
      exec.fence();auto b=Clock::now();
      Csr csr;Vectors force;auto c=b;
#if defined(SUMMIT_MD_CUDA)
      if(tiled){
        if(!raw.membershipOnly){force=ordinary_gpu::force(points,raw.radius,tile);advance(points,velocity,force);}
      }else{
        csr=arborx_query(points,raw.radius);c=Clock::now();
        if(!raw.membershipOnly)force=consume(points,velocity,csr.ids,csr.offsets);
      }
#else
      csr=arm=="arborx"?arborx_query(points,raw.radius):grid_query(points,raw.radius);c=Clock::now();
      if(!raw.membershipOnly)force=consume(points,velocity,csr.ids,csr.offsets);
#endif
      exec.fence();auto e=Clock::now();
#if defined(SUMMIT_MD_CUDA)
      if(tiled){
        // The timed force kernel consumed the original snapshot directly. Audit
        // membership from those same input bytes, not the updated positions.
        Points auditPoints(Kokkos::view_alloc(Kokkos::WithoutInitializing,"audit original points"),n);
        Kokkos::deep_copy(exec,auditPoints,packedPoints);exec.fence();
        csr=ordinary_gpu::audit(auditPoints,raw.radius,tile);
      }
#endif
      // Every offset and sorted row ID (including duplicates) is checked after timing.
      auto actualCsr=host_csr(csr);equal_csr(actualCsr,expectedCsr);std::array<double,3> err{};State actual;
      if(!raw.membershipOnly){actual=state(points,velocity,force);err=equal_state(actual,expected);}
      auto audited=Clock::now();
      csv<<step<<','<<(step<0?"warmup":"measured")<<','<<allocationMs<<','<<(step==-warm?ms(allocated,e):0)<<','<<ms(a,b)<<','<<ms(b,c)<<','<<ms(c,e)<<','<<ms(a,e)<<','<<ms(e,audited)<<','<<ns(a)<<','<<ns(e)<<','<<csr.ids.size()<<','<<err[0]<<','<<err[1]<<','<<err[2]<<",true\n";csv.flush();
      if(step==0||step==reps-1){write_csr(out/("step-"+std::to_string(step)+".csr"),actualCsr);if(!raw.membershipOnly)write_state(out/("step-"+std::to_string(step)+".state"),actual);}
    }
    std::cout<<"PASS all memberships and complete force/velocity/position; "<<ExecutionSpace::name()<<"; "<<arm<<"\n";return 0;
  }catch(std::exception const&e){std::cerr<<e.what()<<'\n';return 2;}
}

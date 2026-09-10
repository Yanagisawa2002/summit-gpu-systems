// Compile the unchanged native driver's helper/registration definitions, including
// its exact generator and float-radius conversion. Its main is also built as
// ArborXNative; this entry only captures/replays the corresponding full-CSR task.
#define main arborx_original_main
#include <bvh_driver.cpp>
#undef main
#include "SnapshotIO.hpp"
#include "../../../PublicBenchmarks/External/Native/ArborXSnapshotBridge.hpp"
using NativeDevice=Kokkos::Device<Kokkos::Serial,Kokkos::HostSpace>;
using NativePoints=Kokkos::View<ArborX::Point<3>*,NativeDevice>;
using NativeTree=ArborX::BoundingVolumeHierarchy<Kokkos::HostSpace,int,NativePoints>;
struct SphereInput {std::vector<std::array<float,3>> points;std::vector<std::array<float,4>> spheres;summit_actual::Csr reference;};
SphereInput load_sphere(std::filesystem::path const& path){
  using namespace summit_actual;
  std::ifstream in(path,std::ios::binary);char magic[8];in.read(magic,8);
  if(std::string(magic,8)!="SMSPH001")throw std::runtime_error("Sphere snapshot ABI");
  auto n=read<std::uint32_t>(in),q=read<std::uint32_t>(in),total=read<std::uint32_t>(in);
  if(n!=50000||q!=20000||total>n*q)throw std::runtime_error("Unexpected native default dimensions");
  SphereInput v;v.points.resize(n);v.spheres.resize(q);
  for(std::uint32_t i=0;i<n;++i){for(auto& x:v.points[i])x=read<float>(in);if(read<std::uint32_t>(in)!=i)throw std::runtime_error("Original native IDs changed");}
  for(auto& s:v.spheres)for(auto& x:s)x=read<float>(in);
  v.reference.offsets.resize(q+1);for(auto& x:v.reference.offsets)x=read<std::uint32_t>(in);
  v.reference.ids.resize(total);for(auto& x:v.reference.ids)x=read<std::uint32_t>(in);
  for(auto const& s:v.spheres)if(s[3]!=v.spheres.front()[3])throw std::runtime_error("Original uniform radius changed");
  if(in.peek()!=EOF)throw std::runtime_error("Trailing snapshot data");return v;
}
int main(int argc,char** argv){
  Kokkos::ScopeGuard guard(argc,argv);Kokkos::Serial exec;
  try {
    if(argc!=4)throw std::runtime_error("ArborXReplay export|replay <snapshot.bin> <new-log.csv>");
    if(std::filesystem::exists(argv[3]))throw std::runtime_error("Output exists");
    std::ofstream output(argv[3]);output<<std::setprecision(17);
    if(std::string(argv[1])=="export"){
      if(std::filesystem::exists(argv[2]))throw std::runtime_error("Snapshot already exists");
      auto start=summit_actual::clock::now();
      auto points=constructPoints<NativeDevice>(50000,ArborXBenchmark::PointCloudType::filled_box);
      auto queries=makeSpatialQueries<NativeDevice>(50000,20000,10,ArborXBenchmark::PointCloudType::filled_box);
      auto generated=summit_actual::clock::now();auto tree=makeTree<NativeTree>(exec,points);
      Kokkos::View<int*,NativeDevice> offsets("offsets",0),ids("ids",0);
      ArborX::query(tree,exec,queries,ids,offsets,ArborX::Experimental::TraversalPolicy().setPredicateSorting(true).setBufferSize(0));exec.fence();
      std::ofstream file(argv[2],std::ios::binary);
      summit_external::write_arborx_snapshot(file,50000,20000,std::uint32_t(ids.size()),
        [&](std::uint32_t i){return summit_external::point{{points(i)[0],points(i)[1],points(i)[2]},i};},
        [&](std::uint32_t i){auto s=ArborX::AccessTraits<decltype(queries)>::get(queries,i)._geometry;return std::array<float,4>{s.centroid()[0],s.centroid()[1],s.centroid()[2],s.radius()};},
        [&](std::uint32_t i){return std::uint32_t(offsets(i));},[&](std::uint32_t i){return std::uint32_t(ids(i));});
      output<<"scope,hostMs,points,queries,totalIds\nnative_generator,"<<summit_actual::ms(start,generated)<<",50000,20000,"<<ids.size()<<'\n';
      std::cout<<"Exported original ArborX filled_box 50000/20000/10/1/0 full CSR: "<<ids.size()<<" IDs\n";
    }else if(std::string(argv[1])=="replay"){
      using namespace summit_actual;auto input=load_sphere(argv[2]);
      NativePoints points("points",50000),queryPoints("queryPoints",20000);
      output<<"case,phase,step,inputCopyMs,indexQueryMs,outputConsumeMs,hostWallMs,checksum,verified\n";
      for(int iteration=-10;iteration<10;++iteration){
        auto start=clock::now();
        for(int i=0;i<50000;++i)for(int d=0;d<3;++d)points(i)[d]=input.points[i][d];
        for(int i=0;i<20000;++i)for(int d=0;d<3;++d)queryPoints(i)[d]=input.spheres[i][d];
        auto queries=ArborX::Experimental::make_intersects(queryPoints,input.spheres.front()[3]);auto copied=clock::now();
        Csr result;clock::time_point queried;
        {auto tree=makeTree<NativeTree>(exec,points);
          Kokkos::View<int*,NativeDevice> offsets("offsets",0),ids("ids",0);
          ArborX::query(tree,exec,queries,ids,offsets,ArborX::Experimental::TraversalPolicy().setPredicateSorting(true).setBufferSize(0));exec.fence();
          queried=clock::now();
          result.offsets.assign(offsets.data(),offsets.data()+offsets.size());result.ids.assign(ids.data(),ids.data()+ids.size());
        }
        auto checksum=consume(result);auto end=clock::now();
        equal(result,input.reference);
        output<<"arborx-default,"<<(iteration<0?"warmup":"measured")<<','<<iteration<<','<<ms(start,copied)<<','<<ms(copied,queried)<<','<<ms(queried,end)<<','<<ms(start,end)<<','<<checksum<<",true\n";
      }
      std::cout<<"PASS complete ArborX full CSR and host-consumed output; Serial backend\n";
    }else throw std::runtime_error("Unknown mode");
    return 0;
  }catch(std::exception const& e){std::cerr<<e.what()<<'\n';return 2;}
}

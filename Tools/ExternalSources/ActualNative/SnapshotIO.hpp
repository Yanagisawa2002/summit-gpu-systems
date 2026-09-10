#pragma once
#include <algorithm>
#include <array>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <map>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>
namespace summit_actual {
using clock = std::chrono::steady_clock;
inline double ms(clock::time_point a,clock::time_point b){return std::chrono::duration<double,std::milli>(b-a).count();}
template<class T> void write(std::ostream& o,T v){o.write(reinterpret_cast<char const*>(&v),sizeof(v));}
template<class T> T read(std::istream& i){T v{};i.read(reinterpret_cast<char*>(&v),sizeof(v));if(!i)throw std::runtime_error("Truncated snapshot");return v;}
struct Csr { std::vector<std::uint32_t> offsets,ids; };
inline std::uint64_t consume(Csr const& c) {
  std::uint64_t result=0;
  for(std::size_t row=0;row+1<c.offsets.size();++row)
    for(auto i=c.offsets[row];i<c.offsets[row+1];++i)
      result+=((std::uint64_t(row)+1)*0x9e3779b1ULL)^(std::uint64_t(c.ids[i])+1);
  return result;
}
inline void equal(Csr actual,Csr expected) {
  if(actual.offsets!=expected.offsets||actual.ids.size()!=expected.ids.size())throw std::runtime_error("Full CSR offsets differ");
  for(std::size_t row=0;row+1<actual.offsets.size();++row){
    std::sort(actual.ids.begin()+actual.offsets[row],actual.ids.begin()+actual.offsets[row+1]);
    std::sort(expected.ids.begin()+expected.offsets[row],expected.ids.begin()+expected.offsets[row+1]);
  }
  if(actual.ids!=expected.ids)throw std::runtime_error("Full CSR membership/multiplicity differs");
}
struct LinkedPoint { double xyz[3];std::uint32_t id; };
struct LinkedInput { std::string name;std::array<double,3> lo,hi,width;std::vector<LinkedPoint> points;Csr reference; };
inline LinkedInput read_linked(std::filesystem::path const& path){
  std::ifstream in(path,std::ios::binary);char magic[8];in.read(magic,8);
  if(std::string(magic,8)!="SMLCL001")throw std::runtime_error("Linked snapshot ABI");
  auto n=read<std::uint32_t>(in),bins=read<std::uint32_t>(in);
  if(n>1000||bins>1000)throw std::runtime_error("Unexpected frozen case");
  LinkedInput v;v.name=path.stem().string();
  for(auto* a:{&v.lo,&v.hi,&v.width})for(auto& x:*a)x=read<double>(in);
  v.points.resize(n);for(auto& p:v.points){for(auto& x:p.xyz)x=read<double>(in);p.id=read<std::uint32_t>(in);}
  v.reference.offsets.resize(bins+1);for(auto& x:v.reference.offsets)x=read<std::uint32_t>(in);
  v.reference.ids.resize(n);for(auto& x:v.reference.ids)x=read<std::uint32_t>(in);
  if(in.peek()!=EOF)throw std::runtime_error("Trailing snapshot bytes");return v;
}
template<class List> Csr linked_csr(List const& list,int n){
  Csr c;c.offsets.reserve(list.totalBins()+1);c.ids.reserve(n);
  for(int i=0;i<list.numBin(0);++i)for(int j=0;j<list.numBin(1);++j)for(int k=0;k<list.numBin(2);++k)c.offsets.push_back(list.binOffset(i,j,k));
  c.offsets.push_back(n);for(int i=0;i<n;++i)c.ids.push_back(list.permutation(i));return c;
}
template<class Positions,class List> void capture_linked(char const* directory,Positions const& x,List const& list,int n,double width,double const* lo,double const* hi,int step){
  std::ostringstream name;name<<"cabana-n"<<std::setw(7)<<std::setfill('0')<<n<<"-w"<<int(width)<<"-t"<<std::setw(2)<<step<<".bin";
  auto path=std::filesystem::path(directory)/name.str();if(std::filesystem::exists(path))throw std::runtime_error("Snapshot already exists");
  std::ofstream out(path,std::ios::binary);out.write("SMLCL001",8);write(out,std::uint32_t(n));write(out,std::uint32_t(list.totalBins()));
  for(int d=0;d<3;++d)write(out,lo[d]);for(int d=0;d<3;++d)write(out,hi[d]);for(int d=0;d<3;++d)write(out,width);
  for(int i=0;i<n;++i){for(int d=0;d<3;++d)write(out,double(x(i,d)));write(out,std::uint32_t(i));}
  auto c=linked_csr(list,n);for(auto v:c.offsets)write(out,v);for(auto v:c.ids)write(out,v);
  if(!out)throw std::runtime_error("Snapshot output failed");
}
}

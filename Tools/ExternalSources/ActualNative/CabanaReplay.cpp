#include <Cabana_Core.hpp>
#include <Kokkos_Core.hpp>
#include "SnapshotIO.hpp"
#include <iostream>
int main(int argc,char** argv){
  Kokkos::ScopeGuard guard(argc,argv);
  try {
    if(argc!=3)throw std::runtime_error("CabanaReplay <snapshot-directory> <new-output.csv>");
    if(std::filesystem::exists(argv[2]))throw std::runtime_error("Output exists");
    std::map<std::string,std::vector<summit_actual::LinkedInput>> cases;
    for(auto const& file:std::filesystem::directory_iterator(argv[1]))if(file.path().extension()==".bin"&&file.path().stem().string().starts_with("cabana-")){
      auto v=summit_actual::read_linked(file.path());auto group=v.name.substr(0,v.name.rfind("-t"));cases[group].push_back(std::move(v));
    }
    if(cases.size()!=4)throw std::runtime_error("Expected exactly four frozen Cabana cases");
    std::ofstream output(argv[2]);output<<"case,phase,step,inputCopyMs,indexMs,outputConsumeMs,hostWallMs,checksum,verified\n"<<std::setprecision(17);
    using namespace summit_actual;
    for(auto& [name,inputs]:cases){
      std::sort(inputs.begin(),inputs.end(),[](auto const& a,auto const& b){return a.name<b.name;});
      if(inputs.size()!=10)throw std::runtime_error("Expected original ten snapshots per case");
      auto const& first=inputs.front();auto n=int(first.points.size());
      Cabana::AoSoA<Cabana::MemberTypes<double[3]>,Kokkos::HostSpace> aosoa("native_positions",n);
      auto positions=Cabana::slice<0>(aosoa);
      auto copy=[&](auto const& in){for(int i=0;i<n;++i)for(int d=0;d<3;++d)positions(i,d)=in.points[i].xyz[d];};
      copy(first);auto list=Cabana::createLinkedCellList(positions,first.width.data(),first.lo.data(),first.hi.data());
      for(int iteration=-10;iteration<10;++iteration){
        auto const& input=inputs[iteration<0?0:iteration];
        auto start=clock::now();copy(input);auto copied=clock::now();
        list.build(positions);Kokkos::fence();auto built=clock::now();
        auto result=linked_csr(list,n);auto checksum=consume(result);auto end=clock::now();
        equal(result,input.reference);
        output<<name<<','<<(iteration<0?"warmup":"measured")<<','<<iteration<<','<<ms(start,copied)<<','<<ms(copied,built)<<','<<ms(built,end)<<','<<ms(start,end)<<','<<checksum<<",true\n";
      }
    }
    std::cout<<"PASS complete Cabana CSR and host-consumed output for four cases; Serial backend\n";
    return 0;
  }catch(std::exception const& e){std::cerr<<e.what()<<'\n';return 2;}
}

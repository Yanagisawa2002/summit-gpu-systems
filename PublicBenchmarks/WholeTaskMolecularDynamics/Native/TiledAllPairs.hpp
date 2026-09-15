// Ordinary CUDA control using Kokkos teams/shared scratch. Not a SUMMIT backend.
// Include after the pinned extracted consumer and the Csr/Points/Vectors aliases.
#pragma once
#if defined(SUMMIT_MD_CUDA)
namespace ordinary_gpu {
constexpr int MaxParticles=32000;
constexpr int MaxAuditIds=64*1024*1024/int(sizeof(int));
enum class Mode { Force, Count, Scatter };
struct AuditCapacityError : std::length_error {
  int required;
  explicit AuditCapacityError(int count):std::length_error("Tiled audit required IDs exceed declared capacity; scatter not launched"),required(count){}
};
using Error=Kokkos::View<int,MemorySpace>;

KOKKOS_INLINE_FUNCTION bool member(ArborX::Point<3> a,ArborX::Point<3> b,float radius) {
  float x=a[0]-b[0],y=a[1]-b[1],z=a[2]-b[2];
  float xx=x*x,yy=y*y,zz=z*z,sum=xx;sum=sum+yy;sum=sum+zz;
  return Kokkos::sqrt(sum)<=radius;
}

template<int Tile,Mode Operation>
void visit(Points points,float radius,Vectors forces,Ints counts,Ints offsets,Ints ids,Error errors) {
  static_assert(Tile==128||Tile==256,"Only the two predeclared candidates are implemented");
  using Policy=Kokkos::TeamPolicy<ExecutionSpace>;
  using Team=Policy::member_type;
  using Scratch=Kokkos::View<ArborX::Point<3>*,ExecutionSpace::scratch_memory_space,
                             Kokkos::MemoryTraits<Kokkos::Unmanaged>>;
  int n=points.extent_int(0);
  if(n<1||n>MaxParticles||radius!=3.f)throw std::invalid_argument("Tiled control input/radius budget");
  ExecutionSpace exec;
  auto policy=Policy(exec,(n+Tile-1)/Tile,Tile,1).set_scratch_size(0,Kokkos::PerTeam(Scratch::shmem_size(Tile)));
  // NVCC requires explicit captures for views first used inside if constexpr.
  Kokkos::parallel_for("OrdinaryGPU::tiled_neighbours",policy,
    [points,radius,n,forces,counts,offsets,ids,errors] KOKKOS_FUNCTION(Team const&team){
    Scratch tile(team.team_scratch(0),Tile);
    int lane=team.team_rank(),q=team.league_rank()*Tile+lane;
    bool active=q<n;
    ArborX::Point<3> query{};
    if(active)query=points(q);
    int matches=0;
    float fxi=0.f,fyi=0.f,fzi=0.f;
    for(int first=0;first<n;first+=Tile){
      if(first+lane<n)tile(lane)=points(first+lane);
      // Inactive query lanes still load tiles and execute BOTH barriers.
      team.team_barrier();
      int available=n-first<Tile?n-first:Tile;
      if(active)for(int slot=0;slot<available;++slot){
        int sourceId=first+slot;
        auto point=tile(slot);
        // Input ABI proves source IDs are original contiguous indices. A
        // coincident distinct ID is retained; only q == sourceId is excluded.
        if(sourceId!=q&&member(query,point,radius)){
          if constexpr(Operation==Mode::Force){
            float dx=query[0]-point[0],dy=query[1]-point[1],dz=query[2]-point[2];
            upstream_pair(dx,dy,dz,fxi,fyi,fzi);
          }
          if constexpr(Operation==Mode::Scatter){
            int at=offsets(q)+matches;
            if(at<offsets(q)||at>=offsets(q+1)||at>=ids.extent_int(0))
              Kokkos::atomic_fetch_or(errors.data(),1);
            else ids(at)=sourceId;
          }
          ++matches;
        }
      }
      team.team_barrier();
    }
    if(active){
      if constexpr(Operation==Mode::Force){forces(q,0)=fxi;forces(q,1)=fyi;forces(q,2)=fzi;}
      if constexpr(Operation==Mode::Count)counts(q)=matches;
      if constexpr(Operation==Mode::Scatter)
        if(matches!=offsets(q+1)-offsets(q))Kokkos::atomic_fetch_or(errors.data(),2);
    }
  });
  exec.fence();
}

template<int Tile> Vectors force(Points points,float radius){
  Vectors result(Kokkos::view_alloc(Kokkos::WithoutInitializing,"ordinary GPU forces"),points.extent_int(0));
  visit<Tile,Mode::Force>(points,radius,result,{}, {}, {}, {});
  return result;
}

template<int Tile> Csr audit(Points originalPoints,float radius,int maxIds=MaxAuditIds){
  int n=originalPoints.extent_int(0);
  if(n<1||n>MaxParticles||maxIds<0||maxIds>MaxAuditIds)
    throw std::invalid_argument("Tiled CSR audit budget");
  // n <= 32000 makes n*(n-1) < INT_MAX. Scan cannot wrap even before
  // the tighter 64 MiB capacity gate, and IDs are allocated only after it.
  Ints counts("audit counts",n),offsets("audit offsets",n+1);
  visit<Tile,Mode::Count>(originalPoints,radius,{},counts,{}, {}, {});
  ExecutionSpace exec;
  Kokkos::parallel_scan("OrdinaryGPU::audit_offsets",Kokkos::RangePolicy(exec,0,n+1),
    KOKKOS_LAMBDA(int i,int &sum,bool final){
      if(final)offsets(i)=sum;
      if(i<n)sum+=counts(i);
    });
  exec.fence();
  int total=0;
  Kokkos::deep_copy(total,Kokkos::subview(offsets,n));
  if(total<0)throw std::runtime_error("Tiled audit count overflow");
  if(total>maxIds)throw AuditCapacityError(total);
  Csr csr{Ints(Kokkos::view_alloc(Kokkos::WithoutInitializing,"audit live IDs"),total),offsets};
  Error errors("tiled audit overflow flags");
  visit<Tile,Mode::Scatter>(originalPoints,radius,{},counts,offsets,csr.ids,errors);
  int error=0;Kokkos::deep_copy(error,errors);
  if(error)throw std::runtime_error("Tiled count/scatter membership or capacity mismatch");
  return csr;
}

inline Vectors force(Points points,float radius,int tile){
  if(tile==128)return force<128>(points,radius);
  if(tile==256)return force<256>(points,radius);
  throw std::invalid_argument("Only tile 128/256 are predeclared");
}
inline Csr audit(Points points,float radius,int tile,int maxIds=MaxAuditIds){
  if(tile==128)return audit<128>(points,radius,maxIds);
  if(tile==256)return audit<256>(points,radius,maxIds);
  throw std::invalid_argument("Only tile 128/256 are predeclared");
}
} // namespace ordinary_gpu
#endif

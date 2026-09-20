// Compile the SAME HLSL predicate as C++ with a minimal vector shim.
// This executes predicate logic, not DXIL or the Unity rendering backend.
#include <algorithm>
#include <array>
#include <cmath>
#include <iostream>
#include <limits>
#include <random>
#include <stdexcept>
#include <string>
using uint = unsigned int;
struct float3 { float x, y, z; };
struct float4 { float3 xyz; float w; };
float3 operator-(float3 a, float3 b) { return {a.x-b.x, a.y-b.y, a.z-b.z}; }
float3 operator/(float3 a, float b) { return {a.x/b, a.y/b, a.z/b}; }
float dot(float3 a, float3 b) { return a.x*b.x+a.y*b.y+a.z*b.z; }
float length(float3 a) { return std::sqrt(dot(a,a)); }
using std::min;
uint _Bfp2CameraFrameCount = 0;
float3 _Bfp2CameraPositionOS = {};
float4 _Bfp2CameraPositionsOS[8] = {};
#include "../../Integrations/NYCGIS/Assets/Shaders/NYCGISDemo/Bfp2NormalConeVisibility.hlsl"

static int assertions = 0;
void check(bool condition, const std::string& name) {
    ++assertions;
    if (!condition) throw std::runtime_error(name);
}
// Independent double-precision oracle: no nearest-view selection, no calls
// into the production helper. Tests keep randomized inputs away from cutoffs.
bool oracle(float3 c, float3 axis, float cutoff, const float3* views, uint n) {
    bool all = true;
    for (uint i=0; i<n; ++i) {
        double dx=double(views[i].x)-c.x, dy=double(views[i].y)-c.y, dz=double(views[i].z)-c.z;
        double d=std::sqrt(dx*dx+dy*dy+dz*dz);
        bool reject = d>double(0.001f) &&
            (double(axis.x)*dx+double(axis.y)*dy+double(axis.z)*dz)/d < -double(cutoff);
        all = all && reject;
    }
    return all;
}
bool run(float3 c, float3 axis, float cutoff, const std::array<float3,8>& views, uint n) {
    _Bfp2CameraFrameCount=n;
    for (uint i=0;i<8;++i) _Bfp2CameraPositionsOS[i].xyz=views[i];
    return Bfp2NormalConeRejectsAllCameras(c,axis,cutoff);
}
int main() {
    try {
        const float3 c={0,0,0}, axis={0,0,1};
        std::array<float3,8> v{};
        v[0]={0,0,-1}; v[1]={0,0,2};
        check(Bfp2NormalConeRejectsView(c,v[0],axis,0), "original nearest-view rejection control");
        check(!run(c,axis,0,v,2), "issue16 union retains accepting farther camera");
        std::swap(v[0],v[1]);
        check(!run(c,axis,0,v,2), "issue16 camera-order reversal");
        v[0]={0,0,-1}; v[1]={0,0,-2};
        check(run(c,axis,0,v,2), "all reject");
        v[1]=c;
        check(!run(c,axis,0,v,2), "zero-distance view retains");
        v[1]={0,0,-0.0005f};
        check(!run(c,axis,0,v,2), "near-zero view retains");
        v[1]={1,0,0};
        check(!run(c,axis,0,v,2), "cutoff equality retains");
        v[1]={0,0,std::numeric_limits<float>::quiet_NaN()};
        check(!run(c,axis,0,v,2), "undefined view retains");
        for (uint i=0;i<8;++i) v[i]={0,0,-float(i+1)};
        v[7]={0,0,9};
        check(!run(c,axis,0,v,8), "eighth view participates");
        check(!run(c,axis,0,v,99), "oversize count obeys eight-view storage contract");
        _Bfp2CameraPositionOS={0,0,-1};
        check(run(c,axis,0,v,0), "zero-count legacy fallback rejects");
        _Bfp2CameraPositionOS={0,0,1};
        check(!run(c,axis,0,v,0), "zero-count legacy fallback retains");
        std::mt19937 rng(0x51ed270b);
        std::uniform_real_distribution<float> coord(-100,100), cut(-0.9f,0.9f);
        for (int t=0;t<10000;++t) {
            float3 center={coord(rng),coord(rng),coord(rng)};
            float3 a={coord(rng),coord(rng),coord(rng)}; a=a/length(a);
            float k=cut(rng); uint n=1+uint(t%8);
            bool nearBoundary=false;
            for (auto& p:v) {
                p={coord(rng),coord(rng),coord(rng)};
                float3 d=p-center;
                nearBoundary |= std::abs(dot(a,d/length(d))+k)<0.0001f;
            }
            if (nearBoundary) continue;
            check(run(center,a,k,v,n)==oracle(center,a,k,v.data(),n), "random independent oracle");
            if(n==1) check(run(center,a,k,v,n)==Bfp2NormalConeRejectsView(center,v[0],a,k), "single-camera equivalence");
            std::reverse(v.begin(),v.begin()+n);
            check(run(center,a,k,v,n)==oracle(center,a,k,v.data(),n), "random reversed views");
        }
        std::cout << "PASS " << assertions << " predicate assertions (CPU; not GPU execution)\n";
        return 0;
    } catch (const std::exception& e) { std::cerr << "FAIL: " << e.what() << '\n'; return 1; }
}

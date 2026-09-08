#pragma once
#include <array>
#include <cstdint>
#include <cstring>
#include <limits>
#include <ostream>
#include <stdexcept>

// Thin hook for original, host-mirrored ArborX generated points/predicates and
// COMPLETE no-callback CSR. Read callbacks must return the unchanged native data.
// There is intentionally no main(), generator, clock, Kokkos initialize or run loop.
namespace summit_external
{
inline void u32(std::ostream& out, std::uint32_t value)
{
    unsigned char b[4] = {static_cast<unsigned char>(value), static_cast<unsigned char>(value>>8),
                         static_cast<unsigned char>(value>>16), static_cast<unsigned char>(value>>24)};
    out.write(reinterpret_cast<char const*>(b), 4);
}
inline void f32(std::ostream& out, float value)
{
    static_assert(sizeof(float)==4 && std::numeric_limits<float>::is_iec559);
    std::uint32_t bits; std::memcpy(&bits, &value, 4); u32(out,bits);
}
struct point { std::array<float,3> xyz; std::uint32_t id; };
template<class ReadPoint, class ReadSphere, class ReadOffset, class ReadId>
void write_arborx_snapshot(std::ostream& out, std::uint32_t n, std::uint32_t q,
    std::uint32_t total, ReadPoint read_point, ReadSphere read_sphere,
    ReadOffset read_offset, ReadId read_id)
{
    if(n>16776960 || q>65535 || total>static_cast<std::uint64_t>(n)*q)
        throw std::invalid_argument("Unsupported complete snapshot size");
    out.write("SMSPH001",8); u32(out,n); u32(out,q); u32(out,total);
    for(std::uint32_t i=0;i<n;i++)
    { point p=read_point(i); for(float x:p.xyz)f32(out,x);u32(out,p.id); }
    for(std::uint32_t i=0;i<q;i++)
    { std::array<float,4> sphere=read_sphere(i); for(float x:sphere)f32(out,x); }
    for(std::uint32_t i=0;i<=q;i++)u32(out,read_offset(i));
    for(std::uint32_t i=0;i<total;i++)u32(out,read_id(i));
    if(!out)throw std::runtime_error("Snapshot write failed");
}
}

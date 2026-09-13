cmake_minimum_required(VERSION 3.22)
project(SummitExternalSnapshotBridge LANGUAGES CXX)
add_library(SummitExternalSnapshotBridge OBJECT CompileSnapshotBridge.cpp)
target_compile_features(SummitExternalSnapshotBridge PRIVATE cxx_std_17)
# No executable, test registration, benchmark framework, or runtime target.

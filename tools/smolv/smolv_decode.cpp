#include "smolv.h"
#include <fstream>
#include <iostream>
#include <iterator>
#include <vector>
int main(int argc, char** argv) {
    if (argc != 3) { std::cerr << "usage: smolv_decode input.smolv output.spv\n"; return 2; }
    std::ifstream input(argv[1], std::ios::binary);
    if (!input) return 3;
    std::vector<char> source((std::istreambuf_iterator<char>(input)), {});
    size_t size = smolv::GetDecodedBufferSize(source.data(), source.size());
    if (!size || size > 64 * 1024 * 1024) return 4;
    std::vector<char> output(size);
    if (!smolv::Decode(source.data(), source.size(), output.data(), output.size())) return 5;
    std::ofstream file(argv[2], std::ios::binary);
    file.write(output.data(), output.size());
    return file ? 0 : 6;
}

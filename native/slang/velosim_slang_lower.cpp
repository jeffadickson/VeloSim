// Optional in-process MikePopoloski/slang C++20 AST bridge for VeloSim.
// Built when -DVELOSIM_SLANG_FRONTEND=ON and find_package(slang CONFIG REQUIRED) succeeds.
// Eliminates Surelog, UHDM, and Python by elaborating SystemVerilog directly in-process
// via slang::driver::Driver and serializing the elaborated AST for VeloSim's SlangAstLowerer.
#include "slang/ast/ASTSerializer.h"
#include "slang/ast/Compilation.h"
#include "slang/driver/Driver.h"
#include "slang/text/Json.h"
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

int main(int argc, char** argv) {
    if (argc < 4) {
        std::cerr << "Usage: velosim_slang_ast <top-module> <output-ast.json> <file.sv> [<file2.sv> ...]\n";
        return 2;
    }
    const std::string top = argv[1];
    const std::string out_path = argv[2];

    slang::driver::Driver driver;
    driver.addStandardArgs();
    std::vector<std::string> args = {argv[0], "--std", "1800-2023", "--top", top};
    for (int i = 3; i < argc; ++i) args.emplace_back(argv[i]);
    std::vector<const char*> c_args;
    c_args.reserve(args.size());
    for (const auto& a : args) c_args.push_back(a.c_str());

    if (!driver.parseCommandLine(static_cast<int>(c_args.size()), c_args.data())) return 1;
    if (!driver.processOptions()) return 1;
    if (!driver.parseAllSources()) return 1;

    auto compilation = driver.createCompilation();
    if (!driver.reportCompilation(*compilation, /* quiet */ false)) return 1;

    slang::JsonWriter writer;
    writer.setPrettyPrint(false);
    slang::ast::ASTSerializer serializer(*compilation, writer);
    writer.startObject();
    writer.writeProperty("design");
    serializer.serialize(compilation->getRoot());
    writer.endObject();

    std::ofstream out(out_path, std::ios::binary);
    if (!out) {
        std::cerr << "Failed to open output file: " << out_path << "\n";
        return 1;
    }
    auto view = writer.view();
    out.write(view.data(), static_cast<std::streamsize>(view.size()));
    return out ? 0 : 1;
}

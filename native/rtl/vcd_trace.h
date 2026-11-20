#pragma once
#include "runtime.h"
#include <fstream>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>

namespace vsrtl {

// IEEE 1364 hierarchical 4-state VCD waveform writer with delta compression.
// Supports arbitrary dotted signal hierarchies and 1..256-bit 4-state words.
class VcdWriter {
 struct Entry {
  size_t sig_id;
  int width;
  std::string code;
  W last;
 };
 std::ofstream out_;
 std::vector<Entry> entries_;
 bool opened_ = false;
 bool dumped_initial_ = false;
 uint64_t last_time_ = 0;

 struct ScopeNode {
  std::map<std::string, ScopeNode> children;
  std::vector<std::tuple<std::string, int, std::string>> vars;
 };

 static std::string make_code(size_t index) {
  std::string code;
  do {
   code.push_back(char(33 + (index % 94)));
   index /= 94;
  } while (index > 0);
  return code;
 }

 static void emit_scope(std::ofstream& out, const std::string& name, const ScopeNode& node) {
  out << "$scope module " << name << " $end\n";
  for (const auto& [leaf, width, code] : node.vars) {
   out << "$var wire " << width << ' ' << code << ' ' << leaf;
   if (width > 1) out << " [" << (width - 1) << ":0]";
   out << " $end\n";
  }
  for (const auto& [child_name, child] : node.children)
   emit_scope(out, child_name, child);
  out << "$upscope $end\n";
 }

 static void write_value(std::ofstream& out, const W& val, const std::string& code) {
  if (val.w == 1) {
   char ch = val.z.limb[0] ? 'z' : val.x.limb[0] ? 'x' : (val.v.limb[0] ? '1' : '0');
   out << ch << code << '\n';
   return;
  }
  std::string bits = text(val);
  // IEEE 1364-2005 compact vector encoding: trim redundant leading 0/x/z bits
  // while preserving at least one bit (and keeping leading '1' intact).
  size_t start = 0;
  if (bits.size() > 1) {
   char lead = bits[0];
   if (lead == '0' || lead == 'x' || lead == 'z') {
    while (start + 1 < bits.size() && bits[start] == lead) ++start;
    if (lead == '0' && bits[start] == '1') {
     // Leading '0' before '1' can be omitted directly in VCD.
    } else if (bits[start] != lead) {
     // Keep one leading 'x'/'z' bit so VCD readers left-extend properly.
     if (lead != '0' && start > 0) --start;
    }
   }
  }
  out << 'b' << bits.substr(start) << ' ' << code << '\n';
 }

public:
 VcdWriter() = default;
 ~VcdWriter() { close(); }
 VcdWriter(const VcdWriter&) = delete;
 VcdWriter& operator=(const VcdWriter&) = delete;

 bool is_open() const noexcept { return opened_; }

 template <class SignalDesc>
 void open(const std::string& path, const std::string& top_name,
           const std::vector<SignalDesc>& signals, const std::string& timescale = "1ns") {
  close();
  out_.open(path, std::ios::binary | std::ios::trunc);
  if (!out_) throw std::runtime_error("failed to open VCD output file: " + path);
  out_ << "$version VeloSim $end\n";
  out_ << "$timescale " << timescale << " $end\n";

  ScopeNode root;
  entries_.clear();
  entries_.reserve(signals.size());
  for (size_t i = 0; i < signals.size(); ++i) {
   std::string code = make_code(i);
   std::string full = signals[i].name ? signals[i].name : ("sig_" + std::to_string(i));
   ScopeNode* cur = &root;
   size_t pos = 0;
   while (true) {
    size_t dot = full.find('.', pos);
    if (dot == std::string::npos) {
     cur->vars.emplace_back(full.substr(pos), signals[i].width, code);
     break;
    }
    std::string seg = full.substr(pos, dot - pos);
    cur = &cur->children[seg];
    pos = dot + 1;
   }
   entries_.push_back(Entry{i, signals[i].width, std::move(code), unknown(signals[i].width)});
  }

  emit_scope(out_, top_name.empty() ? "top" : top_name, root);
  out_ << "$enddefinitions $end\n";
  opened_ = true;
  dumped_initial_ = false;
  last_time_ = 0;
 }

 void dump(uint64_t time, const std::vector<W>& s) {
  if (!opened_) return;
  if (!dumped_initial_) {
   out_ << '#' << time << "\n$dumpvars\n";
   for (auto& e : entries_) {
    if (e.sig_id >= s.size()) continue;
    e.last = s[e.sig_id];
    write_value(out_, e.last, e.code);
   }
   out_ << "$end\n";
   dumped_initial_ = true;
   last_time_ = time;
   return;
  }
  bool header_written = false;
  for (auto& e : entries_) {
   if (e.sig_id >= s.size()) continue;
   const W& cur = s[e.sig_id];
   if (exact(cur, e.last)) continue;
   if (!header_written) {
    out_ << '#' << time << '\n';
    header_written = true;
   }
   e.last = cur;
   write_value(out_, cur, e.code);
  }
  if (time > last_time_) last_time_ = time;
 }

 void flush() {
  if (opened_) out_.flush();
 }

 void close() {
  if (opened_) {
   out_.flush();
   out_.close();
   opened_ = false;
   dumped_initial_ = false;
   entries_.clear();
  }
 }
};

} // namespace vsrtl

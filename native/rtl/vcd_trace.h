#pragma once
#include "runtime.h"
#include "gzip_stream.h"
#include <fstream>
#include <map>
#include <stdexcept>
#include <string>
#include <tuple>
#include <vector>

namespace vsrtl {

// IEEE 1364 hierarchical 4-state VCD waveform writer with delta compression,
// optional signal selection filtering, and optional RFC 1952 gzip compression (.vcd.gz).
// Supports arbitrary dotted signal hierarchies and 1..VSRTL_MAX_WORD_BITS 4-state words.
class VcdWriter {
 struct Entry {
  size_t sig_id;
  int width;
  std::string code;
  W last;
 };
 std::ofstream out_;
 GzipWriter gz_;
 bool use_gzip_ = false;
 std::vector<Entry> entries_;
 bool opened_ = false;
 bool dumped_initial_ = false;
 uint64_t last_time_ = 0;

 struct ScopeNode {
  std::map<std::string, ScopeNode> children;
  std::vector<std::tuple<std::string, int, std::string>> vars;
 };

 void write_str(const std::string& text) {
  if (use_gzip_) {
   gz_.write(text);
  } else {
   out_.write(text.data(), std::streamsize(text.size()));
   if (!out_) throw std::runtime_error("failed writing VCD output stream");
  }
 }

 static std::string make_code(size_t index) {
  std::string code;
  do {
   code.push_back(char(33 + (index % 94)));
   index /= 94;
  } while (index > 0);
  return code;
 }

 void emit_scope(const std::string& name, const ScopeNode& node) {
  std::string buf;
  buf += "$scope module " + name + " $end\n";
  for (const auto& [leaf, width, code] : node.vars) {
   buf += "$var wire " + std::to_string(width) + ' ' + code + ' ' + leaf;
   if (width > 1) buf += " [" + std::to_string(width - 1) + ":0]";
   buf += " $end\n";
  }
  write_str(buf);
  for (const auto& [child_name, child] : node.children)
   emit_scope(child_name, child);
  write_str("$upscope $end\n");
 }

 static void append_value(std::string& out, const W& val, const std::string& code) {
  if (val.w == 1) {
   char ch = val.z.limb[0] ? 'z' : val.x.limb[0] ? 'x' : (val.v.limb[0] ? '1' : '0');
   out.push_back(ch);
   out += code;
   out.push_back('\n');
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
  out.push_back('b');
  out.append(bits, start, bits.size() - start);
  out.push_back(' ');
  out += code;
  out.push_back('\n');
 }

 static bool matches_selector(const std::string& full, const std::string& sel) {
  if (full == sel) return true;
  if (full.size() > sel.size() && full.compare(0, sel.size(), sel) == 0 && full[sel.size()] == '.')
   return true;
  return false;
 }

public:
 VcdWriter() = default;
 ~VcdWriter() {
  try { close(); } catch (...) {}
 }
 VcdWriter(const VcdWriter&) = delete;
 VcdWriter& operator=(const VcdWriter&) = delete;

 bool is_open() const noexcept { return opened_; }
 bool is_compressed() const noexcept { return opened_ && use_gzip_; }

 template <class SignalDesc>
 void open(const std::string& path, const std::string& top_name,
           const std::vector<SignalDesc>& signals, const std::string& timescale = "1ns",
           bool gzip = false, const std::vector<std::string>& filter_prefixes = {}) {
  close();
  if (path.empty()) throw std::invalid_argument("VCD output path must not be empty");
  if (timescale.empty()) throw std::invalid_argument("VCD timescale must not be empty");

  std::vector<bool> matched_selector(filter_prefixes.size(), false);
  for (const auto& sel : filter_prefixes) {
   if (sel.empty()) throw std::invalid_argument("VCD trace selector must not be empty");
  }

  std::vector<size_t> selected_ids;
  selected_ids.reserve(signals.size());
  for (size_t i = 0; i < signals.size(); ++i) {
   std::string full = signals[i].name ? signals[i].name : ("sig_" + std::to_string(i));
   if (filter_prefixes.empty()) {
    selected_ids.push_back(i);
   } else {
    bool hit = false;
    for (size_t k = 0; k < filter_prefixes.size(); ++k) {
     if (matches_selector(full, filter_prefixes[k])) {
      matched_selector[k] = true;
      hit = true;
     }
    }
    if (hit) selected_ids.push_back(i);
   }
  }
  for (size_t k = 0; k < filter_prefixes.size(); ++k) {
   if (!matched_selector[k])
    throw std::invalid_argument("VCD trace selector matched no signals: " + filter_prefixes[k]);
  }

  use_gzip_ = gzip;
  if (use_gzip_) {
   gz_.open(path);
  } else {
   out_.open(path, std::ios::binary | std::ios::trunc);
   if (!out_) throw std::runtime_error("failed to open VCD output file: " + path);
  }

  write_str("$version VeloSim $end\n$timescale " + timescale + " $end\n");

  ScopeNode root;
  entries_.clear();
  entries_.reserve(selected_ids.size());
  for (size_t idx = 0; idx < selected_ids.size(); ++idx) {
   size_t i = selected_ids[idx];
   std::string code = make_code(idx);
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

  emit_scope(top_name.empty() ? "top" : top_name, root);
  write_str("$enddefinitions $end\n");
  opened_ = true;
  dumped_initial_ = false;
  last_time_ = 0;
 }

 void dump(uint64_t time, const std::vector<W>& s) {
  if (!opened_) return;
  std::string buf;
  if (!dumped_initial_) {
   buf += "#" + std::to_string(time) + "\n$dumpvars\n";
   for (auto& e : entries_) {
    if (e.sig_id >= s.size()) continue;
    e.last = s[e.sig_id];
    append_value(buf, e.last, e.code);
   }
   buf += "$end\n";
   write_str(buf);
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
    buf += "#" + std::to_string(time) + "\n";
    header_written = true;
   }
   e.last = cur;
   append_value(buf, cur, e.code);
  }
  if (!buf.empty()) write_str(buf);
  if (time > last_time_) last_time_ = time;
 }

 void flush() {
  if (!opened_) return;
  if (use_gzip_) {
   gz_.flush();
  } else {
   out_.flush();
   if (!out_) throw std::runtime_error("failed flushing VCD output stream");
  }
 }

 void close() {
  if (!opened_) return;
  opened_ = false;
  dumped_initial_ = false;
  entries_.clear();
  if (use_gzip_) {
   gz_.close();
  } else {
   out_.flush();
   bool ok = static_cast<bool>(out_);
   out_.close();
   if (!ok) throw std::runtime_error("failed finalizing VCD output file");
  }
 }
};

} // namespace vsrtl

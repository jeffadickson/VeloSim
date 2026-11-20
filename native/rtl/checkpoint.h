#pragma once
#include "runtime.h"
#include <cstdint>
#include <cstring>
#include <fstream>
#include <stdexcept>
#include <string>
#include <vector>

namespace vsrtl {

// Versioned binary model checkpoint with schema fingerprinting and FNV-1a integrity verification.
// Restores into temporary buffers before committing so corrupted or mismatched files fail closed.
namespace checkpoint {

static constexpr uint32_t kMagic = 0x50435356U; // "VSCP" little-endian
static constexpr uint32_t kVersion = 1U;

inline uint64_t fnv1a_update(uint64_t h, const void* data, size_t len) noexcept {
 const auto* p = static_cast<const uint8_t*>(data);
 for (size_t i = 0; i < len; ++i) {
  h ^= p[i];
  h *= 1099511628211ULL;
 }
 return h;
}

template <class SignalDesc>
inline uint64_t schema_hash(const std::string& model_name,
                            const std::vector<SignalDesc>& signals,
                            const std::vector<std::vector<W>>& mem) noexcept {
 uint64_t h = 14695981039346656037ULL;
 h = fnv1a_update(h, model_name.data(), model_name.size());
 uint64_t sc = signals.size();
 h = fnv1a_update(h, &sc, sizeof(sc));
 for (const auto& sig : signals) {
  const char* n = sig.name ? sig.name : "";
  h = fnv1a_update(h, n, std::strlen(n));
  int32_t meta[3] = {sig.width, sig.direction, int32_t((sig.is_signed ? 1 : 0) | (sig.two_state ? 2 : 0))};
  h = fnv1a_update(h, meta, sizeof(meta));
 }
 uint64_t mc = mem.size();
 h = fnv1a_update(h, &mc, sizeof(mc));
 for (const auto& m : mem) {
  uint64_t depth = m.size();
  h = fnv1a_update(h, &depth, sizeof(depth));
 }
 int32_t max_bits = VSRTL_MAX_WORD_BITS;
 h = fnv1a_update(h, &max_bits, sizeof(max_bits));
 return h;
}

inline void append_bytes(std::vector<uint8_t>& buf, const void* src, size_t n) {
 const auto* p = static_cast<const uint8_t*>(src);
 buf.insert(buf.end(), p, p + n);
}

inline void append_word(std::vector<uint8_t>& buf, const W& w) {
 append_bytes(buf, w.v.limb.data(), sizeof(uint64_t) * Bits::kLimbs);
 append_bytes(buf, w.x.limb.data(), sizeof(uint64_t) * Bits::kLimbs);
 append_bytes(buf, w.z.limb.data(), sizeof(uint64_t) * Bits::kLimbs);
 int32_t width = w.w;
 append_bytes(buf, &width, sizeof(width));
}

inline bool read_bytes(const std::vector<uint8_t>& buf, size_t& pos, void* dst, size_t n) noexcept {
 if (pos + n > buf.size()) return false;
 if (n > 0) std::memcpy(dst, buf.data() + pos, n);
 pos += n;
 return true;
}

inline bool read_word(const std::vector<uint8_t>& buf, size_t& pos, W& out) noexcept {
 int32_t width = 0;
 if (!read_bytes(buf, pos, out.v.limb.data(), sizeof(uint64_t) * Bits::kLimbs) ||
     !read_bytes(buf, pos, out.x.limb.data(), sizeof(uint64_t) * Bits::kLimbs) ||
     !read_bytes(buf, pos, out.z.limb.data(), sizeof(uint64_t) * Bits::kLimbs) ||
     !read_bytes(buf, pos, &width, sizeof(width)))
  return false;
 if (width < 1 || width > VSRTL_MAX_WORD_BITS) return false;
 out.w = width;
 return true;
}

struct Header {
 uint32_t magic;
 uint32_t version;
 uint64_t schema;
 uint64_t time;
 uint32_t sig_count;
 uint32_t mem_count;
 uint64_t payload_bytes;
 uint64_t payload_checksum;
};

template <class SignalDesc>
inline void save(const std::string& path,
                 const std::string& model_name,
                 const std::vector<SignalDesc>& signals,
                 uint64_t now,
                 const std::vector<W>& s,
                 const std::vector<std::vector<W>>& mem,
                 const std::vector<uint8_t>& extra) {
 std::vector<uint8_t> payload;
 payload.reserve(s.size() * 100 + extra.size() + 64);
 for (const auto& w : s) append_word(payload, w);
 for (const auto& m : mem) {
  uint32_t depth = uint32_t(m.size());
  append_bytes(payload, &depth, sizeof(depth));
  for (const auto& cell : m) append_word(payload, cell);
 }
 uint32_t extra_len = uint32_t(extra.size());
 append_bytes(payload, &extra_len, sizeof(extra_len));
 if (!extra.empty()) append_bytes(payload, extra.data(), extra.size());

 Header hdr{};
 hdr.magic = kMagic;
 hdr.version = kVersion;
 hdr.schema = schema_hash(model_name, signals, mem);
 hdr.time = now;
 hdr.sig_count = uint32_t(s.size());
 hdr.mem_count = uint32_t(mem.size());
 hdr.payload_bytes = uint64_t(payload.size());
 hdr.payload_checksum = fnv1a_update(14695981039346656037ULL, payload.data(), payload.size());

 std::ofstream out(path, std::ios::binary | std::ios::trunc);
 if (!out) throw std::runtime_error("failed to open checkpoint file for writing: " + path);
 out.write(reinterpret_cast<const char*>(&hdr), sizeof(hdr));
 if (!payload.empty()) out.write(reinterpret_cast<const char*>(payload.data()), std::streamsize(payload.size()));
 if (!out) throw std::runtime_error("failed writing checkpoint file: " + path);
}

template <class SignalDesc>
inline void load(const std::string& path,
                 const std::string& model_name,
                 const std::vector<SignalDesc>& signals,
                 uint64_t& out_now,
                 std::vector<W>& out_s,
                 std::vector<std::vector<W>>& out_mem,
                 std::vector<uint8_t>& out_extra) {
 std::ifstream in(path, std::ios::binary);
 if (!in) throw std::runtime_error("failed to open checkpoint file for reading: " + path);
 Header hdr{};
 in.read(reinterpret_cast<char*>(&hdr), sizeof(hdr));
 if (!in || hdr.magic != kMagic) throw std::runtime_error("invalid VeloSim checkpoint magic");
 if (hdr.version != kVersion) throw std::runtime_error("unsupported VeloSim checkpoint version");
 const uint64_t expected_schema = schema_hash(model_name, signals, out_mem);
 if (hdr.schema != expected_schema || hdr.sig_count != out_s.size() || hdr.mem_count != out_mem.size())
  throw std::runtime_error("checkpoint model/schema mismatch");
 if (hdr.payload_bytes > 256ULL * 1024ULL * 1024ULL)
  throw std::runtime_error("checkpoint payload exceeds 256 MiB limit");

 std::vector<uint8_t> payload(size_t(hdr.payload_bytes));
 if (!payload.empty()) {
  in.read(reinterpret_cast<char*>(payload.data()), std::streamsize(payload.size()));
  if (!in || size_t(in.gcount()) != payload.size())
   throw std::runtime_error("truncated checkpoint payload");
 }
 char trailing = 0;
 if (in.read(&trailing, 1)) throw std::runtime_error("unexpected trailing bytes in checkpoint");

 const uint64_t actual_cs = fnv1a_update(14695981039346656037ULL, payload.data(), payload.size());
 if (actual_cs != hdr.payload_checksum)
  throw std::runtime_error("checkpoint payload checksum mismatch");

 size_t pos = 0;
 std::vector<W> next_s(hdr.sig_count);
 for (uint32_t i = 0; i < hdr.sig_count; ++i) {
  if (!read_word(payload, pos, next_s[i]) || next_s[i].w != signals[i].width)
   throw std::runtime_error("corrupt signal word in checkpoint");
 }
 std::vector<std::vector<W>> next_mem(hdr.mem_count);
 for (uint32_t i = 0; i < hdr.mem_count; ++i) {
  uint32_t depth = 0;
  if (!read_bytes(payload, pos, &depth, sizeof(depth)) || depth != out_mem[i].size())
   throw std::runtime_error("checkpoint memory depth mismatch");
  next_mem[i].resize(depth);
  for (uint32_t j = 0; j < depth; ++j) {
   if (!read_word(payload, pos, next_mem[i][j]))
    throw std::runtime_error("corrupt memory cell in checkpoint");
  }
 }
 uint32_t extra_len = 0;
 if (!read_bytes(payload, pos, &extra_len, sizeof(extra_len)))
  throw std::runtime_error("truncated checkpoint extra state header");
 std::vector<uint8_t> next_extra(extra_len);
 if (extra_len > 0 && !read_bytes(payload, pos, next_extra.data(), extra_len))
  throw std::runtime_error("truncated checkpoint extra state");
 if (pos != payload.size())
  throw std::runtime_error("unconsumed bytes in checkpoint payload");

 out_now = hdr.time;
 out_s = std::move(next_s);
 out_mem = std::move(next_mem);
 out_extra = std::move(next_extra);
}

} // namespace checkpoint
} // namespace vsrtl

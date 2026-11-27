#pragma once
#include <array>
#include <cstdint>
#include <fstream>
#include <stdexcept>
#include <string>
#include <vector>

namespace vsrtl {

// Zero-dependency RFC 1952 GZIP + RFC 1951 LZ77 fixed-Huffman DEFLATE writer.
// Streams 64 KiB uncompressed blocks so memory usage remains O(1) even for large traces.
class GzipWriter {
 static constexpr size_t kBlockLimit = 65536;
 static constexpr size_t kHashBits = 13;
 static constexpr size_t kHashSize = size_t(1) << kHashBits;
 static constexpr size_t kMaxDist = 32768;
 static constexpr size_t kMaxMatch = 258;

 std::ofstream out_;
 std::vector<uint8_t> buf_;
 std::vector<uint8_t> compressed_;
 std::array<int32_t, kHashSize> head_{};
 std::vector<int32_t> prev_;
 uint64_t bit_buf_ = 0;
 int bit_count_ = 0;
 uint32_t crc_ = 0xFFFFFFFFu;
 uint32_t isize_ = 0;
 bool opened_ = false;
 bool failed_ = false;

 static const std::array<uint32_t, 256>& crc_table() noexcept {
  static const std::array<uint32_t, 256> table = []() {
   std::array<uint32_t, 256> t{};
   for (uint32_t i = 0; i < 256; ++i) {
    uint32_t c = i;
    for (int k = 0; k < 8; ++k)
     c = (c & 1u) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
    t[i] = c;
   }
   return t;
  }();
  return table;
 }

 static constexpr uint32_t reverse_bits(uint32_t code, int bits) noexcept {
  uint32_t out = 0;
  for (int i = 0; i < bits; ++i) {
   out = (out << 1) | (code & 1u);
   code >>= 1;
  }
  return out;
 }

 struct FixedCode { uint16_t bits; uint8_t len; };

 static const std::array<FixedCode, 288>& litlen_table() noexcept {
  static constexpr auto table = []() constexpr {
   std::array<FixedCode, 288> t{};
   for (uint32_t sym = 0; sym <= 143u; ++sym) t[sym] = {uint16_t(reverse_bits(0x30u + sym, 8)), 8};
   for (uint32_t sym = 144u; sym <= 255u; ++sym) t[sym] = {uint16_t(reverse_bits(0x190u + (sym - 144u), 9)), 9};
   for (uint32_t sym = 256u; sym <= 279u; ++sym) t[sym] = {uint16_t(reverse_bits(sym - 256u, 7)), 7};
   for (uint32_t sym = 280u; sym <= 287u; ++sym) t[sym] = {uint16_t(reverse_bits(0xC0u + (sym - 280u), 8)), 8};
   return t;
  }();
  return table;
 }

 static const std::array<uint8_t, 30>& dist_rev_table() noexcept {
  static constexpr auto table = []() constexpr {
   std::array<uint8_t, 30> t{};
   for (uint32_t idx = 0; idx < 30u; ++idx) t[idx] = uint8_t(reverse_bits(idx, 5));
   return t;
  }();
  return table;
 }

 void put_bits(uint32_t val, int nbits) {
  bit_buf_ |= uint64_t(val) << bit_count_;
  bit_count_ += nbits;
  while (bit_count_ >= 8) {
   compressed_.push_back(uint8_t(bit_buf_ & 0xFFu));
   bit_buf_ >>= 8;
   bit_count_ -= 8;
  }
 }

 void emit_litlen(uint32_t sym) {
  const auto c = litlen_table()[sym];
  put_bits(c.bits, c.len);
 }

 void emit_length(uint32_t len) {
  static constexpr uint16_t kBase[29] = {
   3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258
  };
  static constexpr uint8_t kExtra[29] = {
   0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,0
  };
  int idx = 28;
  if (len < 258u) {
   idx = 0;
   while (idx < 27 && len >= kBase[idx + 1]) ++idx;
  }
  emit_litlen(257u + uint32_t(idx));
  if (kExtra[idx]) put_bits(len - kBase[idx], kExtra[idx]);
 }

 void emit_distance(uint32_t dist) {
  static constexpr uint16_t kBase[30] = {
   1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,
   1025,1537,2049,3073,4097,6145,8193,12289,16385,24577
  };
  static constexpr uint8_t kExtra[30] = {
   0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13
  };
  int idx = 0;
  while (idx < 29 && dist >= kBase[idx + 1]) ++idx;
  put_bits(dist_rev_table()[idx], 5);
  if (kExtra[idx]) put_bits(dist - kBase[idx], kExtra[idx]);
 }

 static uint32_t hash3(const uint8_t* p) noexcept {
  uint32_t h = (uint32_t(p[0]) << 16) ^ (uint32_t(p[1]) << 8) ^ uint32_t(p[2]);
  return ((h * 2654435761u) >> (32 - kHashBits)) & uint32_t(kHashSize - 1);
 }

 void flush_block(bool is_final) {
  // BFINAL (1 bit) + BTYPE=01 fixed Huffman (2 bits, LSB first => 1).
  put_bits(is_final ? 1u : 0u, 1);
  put_bits(1u, 2);

  const uint8_t* data = buf_.data();
  const size_t n = buf_.size();
  if (n > 0) {
   head_.fill(-1);
   if (prev_.size() < n) prev_.resize(n);
   std::fill_n(prev_.data(), n, -1);

   size_t i = 0;
   while (i < n) {
    size_t best_len = 0;
    size_t best_dist = 0;
    if (i + 3 <= n) {
     uint32_t h = hash3(data + i);
     int32_t pos = head_[h];
     prev_[i] = pos;
     head_[h] = int32_t(i);

     int chain = 16;
     while (pos >= 0 && chain-- > 0) {
      size_t dist = i - size_t(pos);
      if (dist == 0 || dist > kMaxDist) break;
      size_t limit = std::min(kMaxMatch, n - i);
      if (data[size_t(pos) + best_len] == data[i + best_len]) {
       size_t len = 0;
       while (len < limit && data[size_t(pos) + len] == data[i + len]) ++len;
       if (len >= 3 && len > best_len) {
        best_len = len;
        best_dist = dist;
        if (best_len >= 64 || best_len >= limit) break;
       }
      }
      pos = prev_[size_t(pos)];
     }
    }
    if (best_len >= 3) {
     emit_length(uint32_t(best_len));
     emit_distance(uint32_t(best_dist));
     for (size_t k = 1; k < best_len && i + k + 3 <= n; ++k) {
      uint32_t h = hash3(data + i + k);
      prev_[i + k] = head_[h];
      head_[h] = int32_t(i + k);
     }
     i += best_len;
    } else {
     emit_litlen(data[i]);
     ++i;
    }
   }
  }
  // End-of-block symbol 256.
  emit_litlen(256u);
  if (is_final && bit_count_ > 0) {
   compressed_.push_back(uint8_t(bit_buf_ & 0xFFu));
   bit_buf_ = 0;
   bit_count_ = 0;
  }
  buf_.clear();
  if (!compressed_.empty()) {
   out_.write(reinterpret_cast<const char*>(compressed_.data()), std::streamsize(compressed_.size()));
   compressed_.clear();
   if (!out_) {
    failed_ = true;
    throw std::runtime_error("failed writing compressed gzip stream");
   }
  }
 }

 void write_le32(uint32_t v) {
  uint8_t b[4] = {
   uint8_t(v & 0xFFu),
   uint8_t((v >> 8) & 0xFFu),
   uint8_t((v >> 16) & 0xFFu),
   uint8_t((v >> 24) & 0xFFu)
  };
  out_.write(reinterpret_cast<const char*>(b), 4);
 }

public:
 GzipWriter() = default;
 ~GzipWriter() {
  try { close(); } catch (...) {}
 }
 GzipWriter(const GzipWriter&) = delete;
 GzipWriter& operator=(const GzipWriter&) = delete;

 bool is_open() const noexcept { return opened_; }

 void open(const std::string& path) {
  close();
  out_.open(path, std::ios::binary | std::ios::trunc);
  if (!out_) throw std::runtime_error("failed to open gzip output file: " + path);
  static constexpr uint8_t kHeader[10] = {0x1Fu, 0x8Bu, 0x08u, 0x00u, 0x00u, 0x00u, 0x00u, 0x00u, 0x00u, 0xFFu};
  out_.write(reinterpret_cast<const char*>(kHeader), 10);
  if (!out_) throw std::runtime_error("failed writing gzip header: " + path);
  buf_.clear();
  buf_.reserve(kBlockLimit);
  compressed_.clear();
  compressed_.reserve(kBlockLimit);
  bit_buf_ = 0;
  bit_count_ = 0;
  crc_ = 0xFFFFFFFFu;
  isize_ = 0;
  failed_ = false;
  opened_ = true;
 }

 void write(const char* data, size_t len) {
  if (!opened_) return;
  const auto& tbl = crc_table();
  const auto* bytes = reinterpret_cast<const uint8_t*>(data);
  for (size_t i = 0; i < len; ++i) {
   crc_ = tbl[(crc_ ^ bytes[i]) & 0xFFu] ^ (crc_ >> 8);
  }
  isize_ += uint32_t(len);

  size_t off = 0;
  while (off < len) {
   size_t space = kBlockLimit - buf_.size();
   size_t take = std::min(space, len - off);
   buf_.insert(buf_.end(), bytes + off, bytes + off + take);
   off += take;
   if (buf_.size() == kBlockLimit) flush_block(false);
  }
 }

 void write(const std::string& s) {
  write(s.data(), s.size());
 }

 void flush() {
  if (!opened_) return;
  if (!buf_.empty()) flush_block(false);
  out_.flush();
  if (!out_) {
   failed_ = true;
   throw std::runtime_error("failed flushing gzip output stream");
  }
 }

 void close() {
  if (!opened_) return;
  opened_ = false;
  if (!failed_) {
   flush_block(true);
   write_le32(~crc_);
   write_le32(isize_);
   out_.flush();
   if (!out_) {
    out_.close();
    throw std::runtime_error("failed finalizing gzip output file");
   }
  }
  out_.close();
 }
};

} // namespace vsrtl

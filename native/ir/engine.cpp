// VeloSim native multibit experimental kernel.
// This is not a complete IEEE 1800 scheduler or a UHDM compiler.
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <functional>
#include <limits>
#include <mutex>
#include <thread>
#include <vector>
#include "../include/velosim.h"
#if (defined(__x86_64__) || defined(__i386__)) && (defined(__GNUC__) || defined(__clang__))
#include <immintrin.h>
#define VS_IR_HAS_AVX2 1
#else
#define VS_IR_HAS_AVX2 0
#endif
namespace {
struct Word { uint64_t value, xmask, zmask; uint32_t width; };
struct Proc { uint32_t clk, dst, a, reset, b; uint8_t edge, opcode; };

inline uint64_t mask(uint32_t w) noexcept { return w == 64 ? UINT64_MAX : ((uint64_t{1} << w) - 1); }
inline Word normalize(Word a) noexcept {
 const auto m = mask(a.width);
 a.zmask &= m;
 a.xmask = (a.xmask & m) & ~a.zmask;
 a.value = (a.value & m) & ~(a.xmask | a.zmask);
 return a;
}
inline int scalar(Word a) noexcept { if (a.xmask | a.zmask) return 2; return (a.value & 1) ? 1 : 0; }
inline bool rising(int a, int b) noexcept { return (a == 0 && b != 0) || (a == 2 && b == 1); }
inline bool falling(int a, int b) noexcept { return (a == 1 && b != 1) || (a == 2 && b == 0); }
inline Word resize(Word a, uint32_t w) noexcept { a.width = w; return normalize(a); }
inline Word xword(uint32_t w) noexcept { return Word{0, mask(w), 0, w}; }

// Four-state increment: O(1) fast path for known words, bit-exact carry propagation when X/Z present.
inline Word increment(Word a, uint32_t width) noexcept {
 const auto m = mask(width);
 if (!(a.xmask | a.zmask)) return Word{(a.value + 1) & m, 0, 0, width};
 Word out{0, 0, 0, width};
 int carry = 1;
 for (uint32_t i = 0; i < width; i++) {
  const uint64_t bit = uint64_t{1} << i;
  const int input = ((a.xmask | a.zmask) & bit) ? 2 : ((a.value & bit) ? 1 : 0);
  int sum;
  if (carry == 0) { sum = input; }
  else if (carry == 1) { sum = input == 2 ? 2 : !input; }
  else { sum = 2; }
  if (sum == 1) out.value |= bit;
  if (sum == 2) out.xmask |= bit;
  if (carry == 1) carry = input;
  else if (carry == 2) carry = (input == 0) ? 0 : 2;
 }
 return normalize(out);
}

inline Word compute(const Proc& p, const Word* sig) noexcept {
 const Word a = sig[p.a];
 const auto w = sig[p.dst].width;
 if (p.opcode == 0) return resize(a, w);
 if (p.opcode >= 6) {
  const Word original_b = sig[p.b];
  if (p.opcode >= 10) {
   if (a.xmask | a.zmask | original_b.xmask | original_b.zmask) return xword(w);
   const uint64_t result = p.opcode == 10 ? a.value + original_b.value
                         : p.opcode == 11 ? a.value - original_b.value
                         : a.value * original_b.value;
   return Word{result & mask(w), 0, 0, w};
  }
  const auto m = mask(w);
  const auto ax = (a.xmask | a.zmask) & m, bx = (original_b.xmask | original_b.zmask) & m;
  const auto av = a.value & ~ax & m, bv = original_b.value & ~bx & m;
  const auto az = ~(av | ax) & m, bz = ~(bv | bx) & m;
  if (p.opcode == 6) { const auto one = av & bv; return Word{one, m & ~(one | az | bz), 0, w}; }
  if (p.opcode == 7) { const auto one = av | bv; return Word{one, m & ~(one | (az & bz)), 0, w}; }
  if (p.opcode == 8) { const auto u = ax | bx; return Word{(av ^ bv) & ~u, u, 0, w}; }
  return Word{az, ax, 0, w}; // Bitwise NOT converts input Z to X.
 }
 if (p.opcode == 5) {
  const int r = scalar(sig[p.reset]);
  if (r == 0) return Word{0, 0, 0, w};
  if (a.xmask | a.zmask) return xword(w);
  return Word{(a.value + 1) & mask(w), 0, 0, w};
 }
 if (p.opcode == 4) {
  if (a.xmask | a.zmask) return xword(w);
  return Word{(a.value + 1) & mask(w), 0, 0, w};
 }
 Word next = increment(a, w);
 if (p.opcode == 1) return next;
 if (p.opcode == 2 || p.opcode == 3) {
  const int r = scalar(sig[p.reset]);
  if (r == 0) return Word{0, 0, 0, w};
  if (r == 2 && p.opcode == 2) {
   const uint64_t unknown = next.value | next.xmask | next.zmask;
   return Word{0, unknown, 0, w};
  }
  return next;
 }
 return xword(w);
}

#if VS_IR_HAS_AVX2
inline bool has_avx2() noexcept {
 static const bool ok = __builtin_cpu_supports("avx2");
 return ok;
}

// Direct AVX2 4x64-bit SIMD evaluation for 4 uniform-opcode processes.
__attribute__((target("avx2")))
inline void eval_sv_inc4_avx2(const Proc* procs, const uint32_t* active, const Word* sig, Word* results) noexcept {
 alignas(32) uint64_t av[4], au[4], rv[4], masks[4];
 uint32_t widths[4];
 for (int k = 0; k < 4; ++k) {
  const Proc& p = procs[active[k]];
  const Word& a = sig[p.a];
  widths[k] = sig[p.dst].width;
  masks[k] = mask(widths[k]);
  av[k] = a.value;
  au[k] = a.xmask | a.zmask;
  rv[k] = (p.opcode == 5 && scalar(sig[p.reset]) == 0) ? 0ULL : UINT64_MAX;
 }
 const __m256i v_a = _mm256_load_si256(reinterpret_cast<const __m256i*>(av));
 const __m256i v_au = _mm256_load_si256(reinterpret_cast<const __m256i*>(au));
 const __m256i v_rst = _mm256_load_si256(reinterpret_cast<const __m256i*>(rv));
 const __m256i v_m = _mm256_load_si256(reinterpret_cast<const __m256i*>(masks));
 const __m256i zero = _mm256_setzero_si256();
 const __m256i one = _mm256_set1_epi64x(1);
 const __m256i known = _mm256_cmpeq_epi64(v_au, zero);
 const __m256i inc = _mm256_and_si256(_mm256_add_epi64(v_a, one), v_m);
 const __m256i out_v = _mm256_and_si256(_mm256_and_si256(inc, known), v_rst);
 const __m256i out_x = _mm256_and_si256(_mm256_andnot_si256(known, v_m), v_rst);
 alignas(32) uint64_t ov[4], ox[4];
 _mm256_store_si256(reinterpret_cast<__m256i*>(ov), out_v);
 _mm256_store_si256(reinterpret_cast<__m256i*>(ox), out_x);
 for (int k = 0; k < 4; ++k) results[k] = Word{ov[k], ox[k], 0, widths[k]};
}

__attribute__((target("avx2")))
inline void eval_arith4_avx2(const Proc* procs, const uint32_t* active, const Word* sig, Word* results, bool sub) noexcept {
 alignas(32) uint64_t av[4], bv[4], unk[4], masks[4];
 uint32_t widths[4];
 for (int k = 0; k < 4; ++k) {
  const Proc& p = procs[active[k]];
  const Word& a = sig[p.a];
  const Word& b = sig[p.b];
  widths[k] = sig[p.dst].width;
  masks[k] = mask(widths[k]);
  av[k] = a.value;
  bv[k] = b.value;
  unk[k] = a.xmask | a.zmask | b.xmask | b.zmask;
 }
 const __m256i v_a = _mm256_load_si256(reinterpret_cast<const __m256i*>(av));
 const __m256i v_b = _mm256_load_si256(reinterpret_cast<const __m256i*>(bv));
 const __m256i v_u = _mm256_load_si256(reinterpret_cast<const __m256i*>(unk));
 const __m256i v_m = _mm256_load_si256(reinterpret_cast<const __m256i*>(masks));
 const __m256i known = _mm256_cmpeq_epi64(v_u, _mm256_setzero_si256());
 const __m256i res = sub ? _mm256_sub_epi64(v_a, v_b) : _mm256_add_epi64(v_a, v_b);
 const __m256i out_v = _mm256_and_si256(_mm256_and_si256(res, v_m), known);
 const __m256i out_x = _mm256_andnot_si256(known, v_m);
 alignas(32) uint64_t ov[4], ox[4];
 _mm256_store_si256(reinterpret_cast<__m256i*>(ov), out_v);
 _mm256_store_si256(reinterpret_cast<__m256i*>(ox), out_x);
 for (int k = 0; k < 4; ++k) results[k] = Word{ov[k], ox[k], 0, widths[k]};
}

__attribute__((target("avx2")))
inline void eval_bitwise4_avx2(const Proc* procs, const uint32_t* active, const Word* sig, Word* results, uint8_t op) noexcept {
 alignas(32) uint64_t av[4], ax[4], bv[4], bx[4], masks[4];
 uint32_t widths[4];
 for (int k = 0; k < 4; ++k) {
  const Proc& p = procs[active[k]];
  const Word& a = sig[p.a];
  const Word& b = sig[p.b];
  widths[k] = sig[p.dst].width;
  const uint64_t m = mask(widths[k]);
  masks[k] = m;
  ax[k] = (a.xmask | a.zmask) & m;
  bx[k] = (b.xmask | b.zmask) & m;
  av[k] = a.value & ~ax[k] & m;
  bv[k] = b.value & ~bx[k] & m;
 }
 const __m256i v_a = _mm256_load_si256(reinterpret_cast<const __m256i*>(av));
 const __m256i v_ax = _mm256_load_si256(reinterpret_cast<const __m256i*>(ax));
 const __m256i v_b = _mm256_load_si256(reinterpret_cast<const __m256i*>(bv));
 const __m256i v_bx = _mm256_load_si256(reinterpret_cast<const __m256i*>(bx));
 const __m256i v_m = _mm256_load_si256(reinterpret_cast<const __m256i*>(masks));
 __m256i out_v, out_x;
 if (op == 8) {
  out_x = _mm256_or_si256(v_ax, v_bx);
  out_v = _mm256_andnot_si256(out_x, _mm256_xor_si256(v_a, v_b));
 } else {
  const __m256i ones = _mm256_set1_epi32(-1);
  const __m256i az = _mm256_and_si256(_mm256_xor_si256(_mm256_or_si256(v_a, v_ax), ones), v_m);
  const __m256i bz = _mm256_and_si256(_mm256_xor_si256(_mm256_or_si256(v_b, v_bx), ones), v_m);
  if (op == 6) {
   out_v = _mm256_and_si256(v_a, v_b);
   out_x = _mm256_andnot_si256(_mm256_or_si256(_mm256_or_si256(az, bz), out_v), v_m);
  } else {
   out_v = _mm256_or_si256(v_a, v_b);
   out_x = _mm256_andnot_si256(_mm256_or_si256(out_v, _mm256_and_si256(az, bz)), v_m);
  }
 }
 alignas(32) uint64_t ov[4], ox[4];
 _mm256_store_si256(reinterpret_cast<__m256i*>(ov), out_v);
 _mm256_store_si256(reinterpret_cast<__m256i*>(ox), out_x);
 for (int k = 0; k < 4; ++k) results[k] = Word{ov[k], ox[k], 0, widths[k]};
}
#endif

inline void eval_range(const Proc* procs, const uint32_t* active, const Word* sig, Word* results, size_t begin, size_t end) noexcept {
 size_t j = begin;
#if VS_IR_HAS_AVX2
 if (end - begin >= 4 && has_avx2()) {
  while (j + 4 <= end) {
   const uint8_t op = procs[active[j]].opcode;
   if ((op == 4 || op == 5) &&
       procs[active[j + 1]].opcode == op &&
       procs[active[j + 2]].opcode == op &&
       procs[active[j + 3]].opcode == op) {
    eval_sv_inc4_avx2(procs, active + j, sig, results + j);
    j += 4;
    continue;
   }
   if ((op == 10 || op == 11) &&
       procs[active[j + 1]].opcode == op &&
       procs[active[j + 2]].opcode == op &&
       procs[active[j + 3]].opcode == op) {
    eval_arith4_avx2(procs, active + j, sig, results + j, op == 11);
    j += 4;
    continue;
   }
   if ((op == 6 || op == 7 || op == 8) &&
       procs[active[j + 1]].opcode == op &&
       procs[active[j + 2]].opcode == op &&
       procs[active[j + 3]].opcode == op) {
    eval_bitwise4_avx2(procs, active + j, sig, results + j, op);
    j += 4;
    continue;
   }
   results[j] = compute(procs[active[j]], sig);
   ++j;
  }
 }
#endif
 for (; j < end; ++j) results[j] = compute(procs[active[j]], sig);
}

struct WorkerPool {
 std::mutex mtx;
 std::condition_variable cv_start, cv_done;
 std::vector<std::thread> threads;
 uint64_t generation = 0;
 bool stop = false;
 size_t active_workers = 0;
 size_t total = 0;
 size_t chunk = 1;
 std::atomic<size_t> cursor{0};
 std::atomic<size_t> remaining{0};
 const Proc* procs = nullptr;
 const uint32_t* active = nullptr;
 const Word* sig = nullptr;
 Word* results = nullptr;

 void ensure(size_t helper_count) {
  while (threads.size() < helper_count) {
   const size_t tid = threads.size();
   threads.emplace_back([this, tid]() { loop(tid); });
  }
 }
 void loop(size_t tid) {
  uint64_t seen_gen = 0;
  for (;;) {
   {
    std::unique_lock<std::mutex> lock(mtx);
    cv_start.wait(lock, [&]() { return stop || (generation != seen_gen && tid < active_workers); });
    if (stop) return;
    seen_gen = generation;
   }
   run_chunks();
   if (remaining.fetch_sub(1, std::memory_order_acq_rel) == 1) {
    std::lock_guard<std::mutex> lock(mtx);
    cv_done.notify_one();
   }
  }
 }
 void run_chunks() noexcept {
  for (;;) {
   const size_t start = cursor.fetch_add(chunk, std::memory_order_relaxed);
   if (start >= total) break;
   const size_t stop_at = std::min(start + chunk, total);
   eval_range(procs, active, sig, results, start, stop_at);
  }
 }
 void dispatch(size_t worker_count, const Proc* p, const uint32_t* act, const Word* s, Word* res, size_t n) {
  const size_t helpers = worker_count - 1;
  ensure(helpers);
  {
   std::lock_guard<std::mutex> lock(mtx);
   procs = p; active = act; sig = s; results = res; total = n;
   chunk = std::max<size_t>(4, (n + worker_count * 4 - 1) / (worker_count * 4));
   cursor.store(0, std::memory_order_relaxed);
   remaining.store(helpers, std::memory_order_relaxed);
   active_workers = helpers;
   ++generation;
  }
  cv_start.notify_all();
  run_chunks();
  if (remaining.load(std::memory_order_acquire) != 0) {
   std::unique_lock<std::mutex> lock(mtx);
   cv_done.wait(lock, [&]() { return remaining.load(std::memory_order_acquire) == 0; });
  }
 }
 ~WorkerPool() {
  if (threads.empty()) return;
  {
   std::lock_guard<std::mutex> lock(mtx);
   stop = true;
  }
  cv_start.notify_all();
  for (auto& t : threads) t.join();
 }
};

struct Engine {
 std::vector<Word> sig;
 std::vector<Proc> proc;
 std::vector<std::vector<uint32_t>> rising_procs, falling_procs;
 std::vector<Word> pre;
 std::vector<uint32_t> seen_stamp;
 std::vector<uint32_t> active;
 std::vector<Word> results;
 uint32_t drive_gen = 0;
 uint64_t time = 0;
 WorkerPool pool;
};
}
extern "C" {
uint32_t vs_ir_abi_version() noexcept { return 2; }
uint32_t vs_ir_build_profile() noexcept {
#ifdef NDEBUG
 return 1;
#else
 return 0;
#endif
}
uint64_t vs_ir_ticks(void* p) noexcept { return p ? static_cast<Engine*>(p)->time : 0; }
void* vs_ir_new() noexcept { try { return new Engine; } catch (...) { return nullptr; } }
void vs_ir_free(void* p) noexcept { delete static_cast<Engine*>(p); }
int vs_ir_signal(void* p, uint32_t width, uint64_t value, uint64_t xmask, uint64_t zmask) noexcept {
 if (!p || width < 1 || width > 64 || (xmask & zmask)) return -1;
 try {
  auto& e = *static_cast<Engine*>(p);
  e.sig.push_back(normalize(Word{value, xmask, zmask, width}));
  e.rising_procs.emplace_back();
  e.falling_procs.emplace_back();
  e.seen_stamp.push_back(0);
  return int(e.sig.size() - 1);
 } catch (...) { return -1; }
}
int vs_ir_process_v2(void* p, uint32_t clk, uint32_t dst, uint32_t a, uint32_t b, uint32_t reset, uint8_t edge, uint8_t op) noexcept {
 if (!p || edge > 1 || op > 12) return -1;
 auto& e = *static_cast<Engine*>(p);
 const auto n = e.sig.size();
 if (clk >= n || dst >= n || a >= n || b >= n || reset >= n || e.sig[clk].width != 1) return -1;
 if ((op == 2 || op == 3 || op == 5) && e.sig[reset].width != 1) return -1;
 for (const auto& q : e.proc) if (q.dst == dst) return -2;
 try {
  const uint32_t idx = uint32_t(e.proc.size());
  e.proc.push_back(Proc{clk, dst, a, reset, b, edge, op});
  if (edge == 0) e.rising_procs[clk].push_back(idx);
  else e.falling_procs[clk].push_back(idx);
  return 0;
 } catch (...) { return -1; }
}
int vs_ir_process(void* p, uint32_t clk, uint32_t dst, uint32_t a, uint32_t reset, uint8_t edge, uint8_t op) noexcept {
 if (op > 5) return -1;
 return vs_ir_process_v2(p, clk, dst, a, a, reset, edge, op);
}
int vs_ir_read(void* p, uint32_t id, uint64_t* value, uint64_t* xmask, uint64_t* zmask) noexcept {
 if (!p || !value || !xmask || !zmask) return -1;
 auto& e = *static_cast<Engine*>(p);
 if (id >= e.sig.size()) return -1;
 const auto v = e.sig[id];
 *value = v.value; *xmask = v.xmask; *zmask = v.zmask;
 return int(v.width);
}
int vs_ir_drive(void* p, const uint32_t* ids, const uint64_t* vals, const uint64_t* xs, const uint64_t* zs, uint32_t n, uint32_t workers) noexcept {
 if (!p || workers < 1 || workers > 256 || (n && (!ids || !vals || !xs || !zs))) return -1;
 try {
  auto& e = *static_cast<Engine*>(p);
  const size_t sig_count = e.sig.size();
  if (++e.drive_gen == 0) {
   std::fill(e.seen_stamp.begin(), e.seen_stamp.end(), 0);
   e.drive_gen = 1;
  }
  const uint32_t gen = e.drive_gen;
  e.active.clear();
  for (uint32_t i = 0; i < n; i++) {
   const uint32_t id = ids[i];
   if (id >= sig_count || e.seen_stamp[id] == gen || (xs[i] & zs[i])) return -2;
   e.seen_stamp[id] = gen;
   const auto& rp = e.rising_procs[id];
   const auto& fp = e.falling_procs[id];
   if (!rp.empty() || !fp.empty()) {
    const int before = scalar(e.sig[id]);
    const int after = scalar(normalize(Word{vals[i], xs[i], zs[i], e.sig[id].width}));
    if (!rp.empty() && rising(before, after)) e.active.insert(e.active.end(), rp.begin(), rp.end());
    if (!fp.empty() && falling(before, after)) e.active.insert(e.active.end(), fp.begin(), fp.end());
   }
  }
  // Fast path: no triggered processes (e.g. falling edge or data-only drive).
  if (e.active.empty()) {
   for (uint32_t i = 0; i < n; i++) {
    const uint32_t id = ids[i];
    e.sig[id] = normalize(Word{vals[i], xs[i], zs[i], e.sig[id].width});
   }
   e.time++;
   return 0;
  }
  e.pre.resize(sig_count);
  std::memcpy(e.pre.data(), e.sig.data(), sig_count * sizeof(Word));
  for (uint32_t i = 0; i < n; i++) {
   const uint32_t id = ids[i];
   e.pre[id] = normalize(Word{vals[i], xs[i], zs[i], e.pre[id].width});
  }
  const size_t active_count = e.active.size();
  e.results.resize(active_count);
  const size_t count = std::min<size_t>(workers, active_count);
  if (count <= 1 || active_count < 2048) {
   eval_range(e.proc.data(), e.active.data(), e.pre.data(), e.results.data(), 0, active_count);
  } else {
   e.pool.dispatch(count, e.proc.data(), e.active.data(), e.pre.data(), e.results.data(), active_count);
  }
  for (size_t j = 0; j < active_count; j++) e.pre[e.proc[e.active[j]].dst] = e.results[j];
  e.sig.swap(e.pre);
  e.time++;
  return int(active_count);
 } catch (...) { return -1; }
}
}

// VeloSim v0.10: experimental single-bit 4-state edge scheduler.
// Independent process evaluation may run on multiple threads; NBA-style commit is simultaneous.
// Not a full IEEE SystemVerilog event scheduler.
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>
#include "include/velosim.h"
namespace {
struct Process { uint32_t clk, dst, a, b; uint8_t edge, op; };
constexpr uint8_t X = 2;
inline bool rising(uint8_t a, uint8_t b) noexcept { return (a == 0 && b != 0) || (a >= 2 && b == 1); }
inline bool falling(uint8_t a, uint8_t b) noexcept { return (a == 1 && b != 1) || (a >= 2 && b == 0); }
inline uint8_t calc(const Process& p, const uint8_t* v) noexcept {
 const uint8_t a = v[p.a], b = v[p.b];
 switch (p.op) {
 case 0: return a;
 case 1: return a == 0 || b == 0 ? 0 : a == 1 && b == 1 ? 1 : X;
 case 2: return a == 1 || b == 1 ? 1 : a == 0 && b == 0 ? 0 : X;
 case 3: return a >= 2 || b >= 2 ? X : a ^ b;
 case 4: return a >= 2 ? X : !a;
 default: return X;
 }
}
struct WorkerPool {
 std::mutex mtx;
 std::condition_variable cv_start, cv_done;
 std::vector<std::thread> threads;
 uint64_t generation = 0;
 bool stop = false;
 size_t active_workers = 0, total = 0, chunk = 1;
 alignas(64) std::atomic<size_t> cursor{0};
 alignas(64) std::atomic<size_t> remaining{0};
 const Process* procs = nullptr;
 const uint32_t* active = nullptr;
 const uint8_t* pre = nullptr;
 uint8_t* results = nullptr;

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
   for (size_t j = start; j < stop_at; ++j) results[j] = calc(procs[active[j]], pre);
  }
 }
 void dispatch(size_t worker_count, const Process* p, const uint32_t* act, const uint8_t* s, uint8_t* res, size_t n) {
  const size_t helpers = worker_count - 1;
  ensure(helpers);
  {
   std::lock_guard<std::mutex> lock(mtx);
   procs = p; active = act; pre = s; results = res; total = n;
   chunk = std::max<size_t>(16, (n + worker_count * 4 - 1) / (worker_count * 4));
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
struct Sim {
 std::vector<uint8_t> sig, pre, results, dst_used;
 std::vector<uint32_t> seen_stamp, active;
 std::vector<Process> procs;
 std::vector<std::vector<uint32_t>> rising_procs, falling_procs;
 uint32_t drive_gen = 0;
 uint64_t tick = 0;
 WorkerPool pool;
};
}
extern "C" {
uint32_t vs_sched_abi_version() noexcept { return 1; }
void* vs_sched_new(uint32_t n) noexcept {
 try {
  if (!n || n > 1000000) return nullptr;
  auto s = std::make_unique<Sim>();
  s->sig.assign(n, X);
  s->seen_stamp.assign(n, 0);
  s->dst_used.assign(n, 0);
  s->rising_procs.resize(n);
  s->falling_procs.resize(n);
  return s.release();
 } catch (...) { return nullptr; }
}
void vs_sched_free(void* p) noexcept { delete static_cast<Sim*>(p); }
int vs_sched_get(void* p, uint32_t id) noexcept {
 if (!p) return -1;
 auto& s = *static_cast<Sim*>(p);
 return id < s.sig.size() ? s.sig[id] : -1;
}
uint64_t vs_sched_ticks(void* p) noexcept { return p ? static_cast<Sim*>(p)->tick : 0; }
int vs_sched_add(void* p, uint32_t clk, uint32_t dst, uint32_t a, uint32_t b, uint8_t edge, uint8_t op) noexcept {
 if (!p || edge > 1 || op > 4) return -1;
 auto& s = *static_cast<Sim*>(p);
 if (clk >= s.sig.size() || dst >= s.sig.size() || a >= s.sig.size() || b >= s.sig.size()) return -1;
 if (s.dst_used[dst]) return -2;
 try {
  const uint32_t idx = uint32_t(s.procs.size());
  s.procs.push_back({clk, dst, a, b, edge, op});
  if (edge == 0) s.rising_procs[clk].push_back(idx);
  else s.falling_procs[clk].push_back(idx);
  s.dst_used[dst] = 1;
  return 0;
 } catch (...) { return -1; }
}
int vs_sched_drive(void* p, const uint32_t* ids, const uint8_t* vals, uint32_t n, uint32_t workers) noexcept {
 if (!p || (!ids && n) || (!vals && n) || workers == 0 || workers > 256) return -1;
 try {
  auto& s = *static_cast<Sim*>(p);
  const size_t sig_count = s.sig.size();
  if (++s.drive_gen == 0) {
   std::fill(s.seen_stamp.begin(), s.seen_stamp.end(), 0);
   s.drive_gen = 1;
  }
  const uint32_t gen = s.drive_gen;
  s.active.clear();
  for (uint32_t i = 0; i < n; i++) {
   const uint32_t id = ids[i];
   if (id >= sig_count || vals[i] > 3 || s.seen_stamp[id] == gen) return -2;
   s.seen_stamp[id] = gen;
   const auto& rp = s.rising_procs[id];
   const auto& fp = s.falling_procs[id];
   if (!rp.empty() && rising(s.sig[id], vals[i])) s.active.insert(s.active.end(), rp.begin(), rp.end());
   if (!fp.empty() && falling(s.sig[id], vals[i])) s.active.insert(s.active.end(), fp.begin(), fp.end());
  }
  if (s.active.empty()) {
   for (uint32_t i = 0; i < n; i++) s.sig[ids[i]] = vals[i];
   s.tick++;
   return 0;
  }
  s.pre.resize(sig_count);
  std::memcpy(s.pre.data(), s.sig.data(), sig_count);
  for (uint32_t i = 0; i < n; i++) s.pre[ids[i]] = vals[i];
  const size_t active_count = s.active.size();
  s.results.resize(active_count);
  const size_t count = std::min<size_t>(workers, active_count);
  if (count <= 1 || active_count < 4096) {
   for (size_t j = 0; j < active_count; j++) s.results[j] = calc(s.procs[s.active[j]], s.pre.data());
  } else {
   s.pool.dispatch(count, s.procs.data(), s.active.data(), s.pre.data(), s.results.data(), active_count);
  }
  for (size_t j = 0; j < active_count; j++) s.pre[s.procs[s.active[j]].dst] = s.results[j];
  s.sig.swap(s.pre);
  s.tick++;
  return static_cast<int>(active_count);
 } catch (...) { return -1; }
}
}

#pragma once
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstddef>
#include <exception>
#include <mutex>
#include <thread>
#include <vector>

namespace vsrtl {

// Deterministic barrier-synchronized worker pool for levelized combinational DAG evaluation.
// Uses zero-allocation function-pointer dispatch, cache-line padded atomics (no false sharing),
// static contiguous range partitioning per worker, and a brief spin before sleeping on the
// completion barrier.
class ThreadPool {
 using RangeInvoker = void(*)(const void* ctx, size_t begin, size_t end);

 std::vector<std::thread> workers_;
 std::mutex mtx_;
 std::condition_variable cv_work_;
 std::condition_variable cv_done_;
 RangeInvoker invoker_ = nullptr;
 const void* task_ctx_ = nullptr;
 size_t task_count_ = 0;
 size_t active_participants_ = 1;
 size_t generation_ = 0;
 bool stopping_ = false;
 alignas(64) std::atomic<size_t> remaining_{0};
 alignas(64) std::mutex err_mtx_;
 std::exception_ptr first_error_;

 void run_slice(size_t worker_id, size_t participants, size_t count, RangeInvoker invoker, const void* ctx) noexcept {
  const size_t base = count / participants;
  const size_t extra = count % participants;
  const size_t begin = worker_id * base + std::min(worker_id, extra);
  const size_t end = begin + base + (worker_id < extra ? 1 : 0);
  if (begin >= end) return;
  try {
   invoker(ctx, begin, end);
  } catch (...) {
   std::lock_guard<std::mutex> lk(err_mtx_);
   if (!first_error_) first_error_ = std::current_exception();
  }
 }

 void worker_loop(size_t worker_id) {
  size_t seen_gen = 0;
  while (true) {
   RangeInvoker invoker = nullptr;
   const void* ctx = nullptr;
   size_t count = 0;
   size_t participants = 1;
   {
    std::unique_lock<std::mutex> lk(mtx_);
    cv_work_.wait(lk, [&] { return stopping_ || generation_ != seen_gen; });
    if (stopping_) return;
    seen_gen = generation_;
    invoker = invoker_;
    ctx = task_ctx_;
    count = task_count_;
    participants = active_participants_;
   }
   if (worker_id < participants) {
    run_slice(worker_id, participants, count, invoker, ctx);
   }
   if (remaining_.fetch_sub(1, std::memory_order_acq_rel) == 1) {
    std::lock_guard<std::mutex> lk(mtx_);
    cv_done_.notify_one();
   }
  }
 }

public:
 explicit ThreadPool(size_t num_threads) {
  size_t n = num_threads > 1 ? num_threads - 1 : 0;
  workers_.reserve(n);
  for (size_t i = 0; i < n; ++i) workers_.emplace_back([this, id = i + 1] { worker_loop(id); });
 }

 ~ThreadPool() {
  {
   std::lock_guard<std::mutex> lk(mtx_);
   stopping_ = true;
  }
  cv_work_.notify_all();
  for (auto& t : workers_) {
   if (t.joinable()) t.join();
  }
 }

 ThreadPool(const ThreadPool&) = delete;
 ThreadPool& operator=(const ThreadPool&) = delete;

 size_t thread_count() const noexcept { return workers_.size() + 1; }

 template <class Fn>
 void parallel_for(size_t count, Fn&& fn) {
  if (count == 0) return;
  if (count == 1 || workers_.empty()) {
   for (size_t i = 0; i < count; ++i) fn(i);
   return;
  }
  using DecayFn = std::remove_reference_t<Fn>;
  const RangeInvoker invoker = [](const void* raw, size_t begin, size_t end) {
   auto& f = *const_cast<DecayFn*>(static_cast<const DecayFn*>(raw));
   for (size_t i = begin; i < end; ++i) f(i);
  };
  const void* ctx = static_cast<const void*>(&fn);
  const size_t participants = std::min(workers_.size() + 1, count);
  first_error_ = nullptr;
  remaining_.store(workers_.size(), std::memory_order_relaxed);
  {
   std::lock_guard<std::mutex> lk(mtx_);
   invoker_ = invoker;
   task_ctx_ = ctx;
   task_count_ = count;
   active_participants_ = participants;
   ++generation_;
  }
  cv_work_.notify_all();
  run_slice(0, participants, count, invoker, ctx);
  for (int spin = 0; spin < 64 && remaining_.load(std::memory_order_acquire) != 0; ++spin) {
   std::this_thread::yield();
  }
  if (remaining_.load(std::memory_order_acquire) != 0) {
   std::unique_lock<std::mutex> lk(mtx_);
   cv_done_.wait(lk, [&] { return remaining_.load(std::memory_order_acquire) == 0; });
  }
  if (first_error_) std::rethrow_exception(first_error_);
 }
};

} // namespace vsrtl

#pragma once
#include <atomic>
#include <condition_variable>
#include <cstddef>
#include <exception>
#include <functional>
#include <mutex>
#include <thread>
#include <vector>

namespace vsrtl {

// Deterministic barrier-synchronized worker pool for levelized combinational DAG evaluation.
// The calling thread participates as worker 0; worker threads execute independent tasks within
// a topological level and synchronize before advancing to the next level.
class ThreadPool {
 std::vector<std::thread> workers_;
 std::mutex mtx_;
 std::condition_variable cv_work_;
 std::condition_variable cv_done_;
 std::function<void(size_t)> task_;
 size_t task_count_ = 0;
 std::atomic<size_t> next_index_{0};
 size_t generation_ = 0;
 size_t active_workers_ = 0;
 bool stopping_ = false;
 std::mutex err_mtx_;
 std::exception_ptr first_error_;

 void run_items(size_t count, const std::function<void(size_t)>& fn) noexcept {
  while (true) {
   size_t idx = next_index_.fetch_add(1, std::memory_order_relaxed);
   if (idx >= count) break;
   try {
    fn(idx);
   } catch (...) {
    std::lock_guard<std::mutex> lk(err_mtx_);
    if (!first_error_) first_error_ = std::current_exception();
   }
  }
 }

 void worker_loop() {
  size_t seen_gen = 0;
  while (true) {
   std::function<void(size_t)> fn;
   size_t count = 0;
   {
    std::unique_lock<std::mutex> lk(mtx_);
    cv_work_.wait(lk, [&] { return stopping_ || generation_ != seen_gen; });
    if (stopping_) return;
    seen_gen = generation_;
    fn = task_;
    count = task_count_;
   }
   run_items(count, fn);
   {
    std::lock_guard<std::mutex> lk(mtx_);
    if (--active_workers_ == 0) cv_done_.notify_one();
   }
  }
 }

public:
 explicit ThreadPool(size_t num_threads) {
  size_t n = num_threads > 1 ? num_threads - 1 : 0;
  workers_.reserve(n);
  for (size_t i = 0; i < n; ++i) workers_.emplace_back([this] { worker_loop(); });
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
  first_error_ = nullptr;
  next_index_.store(0, std::memory_order_relaxed);
  std::function<void(size_t)> wrapped = [&](size_t i) { fn(i); };
  {
   std::lock_guard<std::mutex> lk(mtx_);
   task_ = wrapped;
   task_count_ = count;
   active_workers_ = workers_.size();
   ++generation_;
  }
  cv_work_.notify_all();
  run_items(count, wrapped);
  {
   std::unique_lock<std::mutex> lk(mtx_);
   cv_done_.wait(lk, [&] { return active_workers_ == 0; });
   task_ = nullptr;
  }
  if (first_error_) std::rethrow_exception(first_error_);
 }
};

} // namespace vsrtl

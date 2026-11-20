#pragma once
#include <cstddef>
#include <cstdint>
#include <limits>
#include <map>
#include <optional>
#include <stdexcept>
#include <tuple>

namespace vsrtl {
// Deterministic transport queue for future suspended-process lowering. This
// deliberately does not claim the SystemVerilog region/iteration algorithm.
// Payloads are opaque process IDs; executing/resuming them is the caller's job.
class TimedEvents {
public:
 enum class Region : unsigned { active, inactive, nba };
 struct Event { uint64_t time,delta;Region region;uint64_t process; };
private:
 using Key=std::tuple<uint64_t,uint64_t,Region,uint64_t>;
 std::map<Key,uint64_t> pending_;
 uint64_t time_=0,delta_=0,serial_=0;
 Region region_=Region::active;
 bool started_=false;
 size_t remaining_;
public:
 static constexpr size_t capacity=20000;
 explicit TimedEvents(size_t budget=capacity):remaining_(budget) {}
 uint64_t now() const { return time_; }
 size_t size() const { return pending_.size(); }
 // Same-time work targeting a prior region starts a new delta. Zero delay is
 // distinct from a future tick. All overflow/rejection checks precede mutation.
 void schedule(uint64_t delay,Region region,uint64_t process) {
  if(region>Region::nba)throw std::invalid_argument("event region");
  if(delay>std::numeric_limits<uint64_t>::max()-time_)throw std::overflow_error("event time");
  if(pending_.size()==capacity)throw std::length_error("event queue capacity");
  if(serial_==std::numeric_limits<uint64_t>::max())throw std::overflow_error("event sequence");
  uint64_t delta=delay?0:delta_;
  if(!delay&&started_&&region<region_) {
   if(delta==std::numeric_limits<uint64_t>::max())throw std::overflow_error("event delta");
   ++delta;
  }
  pending_.emplace(Key{time_+delay,delta,region,serial_},process);++serial_;
 }
 // Budget is lifetime-wide, so draining and re-enqueuing cannot bypass it.
 // No callback runs here; exceptions never lose an event or advance time.
 std::optional<Event> pop() {
  if(pending_.empty())return std::nullopt;
  if(!remaining_)throw std::runtime_error("event execution budget exceeded");
  const auto& [key,process]=*pending_.begin();
  auto [time,delta,region,sequence]=key;
  Event result{time,delta,region,process};
  pending_.erase(pending_.begin());--remaining_;
  time_=time;delta_=delta;region_=region;started_=true;return result;
 }
};
}

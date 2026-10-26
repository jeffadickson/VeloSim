#pragma once
#include "runtime.h"
#include <algorithm>
#include <deque>
#include <map>

namespace vsrtl {
// Internal storage primitives, not yet an HDL lowering contract. Each instance
// owns its cells; copying a model snapshot therefore copies container contents.
// Limits bound host allocation independently of an HDL declaration's bounds.
class DynamicArray {
 int width_;
 bool two_state_;
 std::vector<W> cells_;
 W initial() const { return two_state_ ? word(0,0,0,width_) : unknown(width_); }
 W convert(W value) const { value=resize(value,width_); return two_state_ ? two_state(value) : value; }
public:
 static constexpr size_t capacity=256;
 explicit DynamicArray(int width,bool two_state=false):width_(width),two_state_(two_state) {
  if(width<1||width>256)throw std::invalid_argument("container element width");
 }
 size_t size() const { return cells_.size(); }
 // new[n](old) retains a prefix; ordinary new[n] initializes every cell.
 // Construct before swapping so rejected/failed allocations preserve old state.
 void allocate(size_t count,bool preserve=false) {
  if(count>capacity)throw std::length_error("dynamic array capacity");
  std::vector<W> next(count,initial());
  if(preserve)std::copy_n(cells_.begin(),std::min(count,cells_.size()),next.begin());
  cells_.swap(next);
 }
 void clear() { cells_.clear(); }
 W read(size_t index) const { return index<cells_.size()?cells_[index]:initial(); }
 void write(size_t index,W value) { if(index<cells_.size())cells_[index]=convert(value); }
};

// Queue overflow is a resource failure, not a model of bounded-queue [$:N]
// truncation. HDL adapters must distinguish those two contracts explicitly.
class Queue {
 DynamicArray type_;
 std::deque<W> cells_;
 W convert(W value) const { auto copy=type_;copy.allocate(1);copy.write(0,value);return copy.read(0); }
public:
 explicit Queue(int width,bool two_state=false):type_(width,two_state) {}
 size_t size() const { return cells_.size(); }
 W read(size_t index) const { return index<cells_.size()?cells_[index]:type_.read(0); }
 void write(size_t index,W value) { if(index<cells_.size())cells_[index]=convert(value); }
 void push(W value,bool front=false) {
  if(cells_.size()==DynamicArray::capacity)throw std::length_error("queue capacity");
  value=convert(value);if(front)cells_.push_front(value);else cells_.push_back(value);
 }
 W pop(bool front=false) {
  if(cells_.empty())return type_.read(0);
  W value=front?cells_.front():cells_.back();
  if(front)cells_.pop_front();else cells_.pop_back();return value;
 }
 void clear() { cells_.clear(); }
};

// Only canonical unsigned integral keys are represented here. Signed/string
// ordering, unknown keys, iteration methods and wildcard keys need HDL adapters.
class AssociativeArray {
 DynamicArray type_;
 std::map<Bits,W> cells_;
public:
 explicit AssociativeArray(int width,bool two_state=false):type_(width,two_state) {}
 size_t size() const { return cells_.size(); }
 bool exists(Bits key) const { return cells_.contains(key); }
 W read(Bits key) const { auto it=cells_.find(key);return it==cells_.end()?type_.read(0):it->second; }
 void write(Bits key,W value) {
  if(!exists(key)&&cells_.size()==DynamicArray::capacity)throw std::length_error("associative array capacity");
  auto copy=type_;copy.allocate(1);copy.write(0,value);cells_.insert_or_assign(key,copy.read(0));
 }
 void erase(Bits key) { cells_.erase(key); }
 void clear() { cells_.clear(); }
};
}

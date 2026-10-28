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
 ElementType type_;
 std::vector<W> cells_;
public:
 static constexpr size_t capacity=container_capacity;
 explicit DynamicArray(int width,bool two_state=false):type_(width,two_state) {}
 size_t size() const { return cells_.size(); }
 // new[n](old) retains a prefix; ordinary new[n] initializes every cell.
 // Construct before swapping so rejected/failed allocations preserve old state.
 void allocate(size_t count,bool preserve=false) {
  if(count>capacity)throw std::length_error("dynamic array capacity");
  dynamic_allocate(cells_,word(count,0,0,64),type_.width(),type_.is_two_state(),preserve,false);
 }
 void clear() { cells_.clear(); }
 W read(size_t index) const { return dynamic_read(cells_,word(index,0,0,64),type_.width(),type_.is_two_state()); }
 void write(size_t index,W value) { if(index<cells_.size())cells_[index]=type_.convert(value); }
};

// Queue overflow is a resource failure, not a model of bounded-queue [$:N]
// truncation. HDL adapters must distinguish those two contracts explicitly.
class Queue {
 ElementType type_;
 std::deque<W> cells_;
public:
 explicit Queue(int width,bool two_state=false):type_(width,two_state) {}
 size_t size() const { return cells_.size(); }
 W read(size_t index) const { return index<cells_.size()?cells_[index]:type_.initial(); }
 void write(size_t index,W value) { if(index<cells_.size())cells_[index]=type_.convert(value); }
 void push(W value,bool front=false) {
  queue_push(cells_,value,type_.width(),type_.is_two_state(),front);
 }
 W pop(bool front=false) {
  return queue_pop(cells_,type_.width(),type_.is_two_state(),front);
 }
 void clear() { cells_.clear(); }
};

// Only canonical unsigned integral keys are represented here. Signed/string
// ordering, unknown keys, iteration methods and wildcard keys need HDL adapters.
class AssociativeArray {
 ElementType type_;
 AssociativeStorage cells_;
public:
 explicit AssociativeArray(int width,bool two_state=false):type_(width,two_state) {}
 size_t size() const { return cells_.size(); }
 bool exists(Bits key) const { return cells_.contains(key); }
 W read(Bits key) const { return assoc_read(cells_,word(key,0,0,256),256,false,type_.width(),type_.is_two_state()); }
 void write(Bits key,W value) {
  assoc_write(cells_,word(key,0,0,256),value,256,false,type_.width(),type_.is_two_state());
 }
 void erase(Bits key) { cells_.erase(key); }
 void clear() { cells_.clear(); }
};
}

#pragma once
#include "runtime.h"
#include "vpi_user.h"
#include "svdpi.h"
#include <functional>
#include <memory>
#include <thread>
#include <utility>
#ifndef VS_VPI_API
#define VS_VPI_API
#endif
namespace vsrtl {
struct Signal { const char* name; int width, direction; bool is_signed=false; bool two_state=false; };
struct CoveragePoint { const char* id; const char* kind; uint64_t hits; };
class Port;
class VS_VPI_API Model {
 friend class Port;
 struct State;
 std::unique_ptr<State> state;
protected:
 std::thread::id owner_thread;
 bool fast_eligible = false;
 bool fast_eval_ok = false;
 bool fast_port_ok = true;
 bool fast_evaluating = false;
 bool fast_dirty = true;
 void evalSlow();
 void refreshFastFlags() noexcept;
 inline bool canFastPortWrite() const noexcept {
  return fast_port_ok && owner_thread == std::this_thread::get_id();
 }
 inline void markDirty() noexcept { fast_dirty = true; }
 inline W fastGet(size_t id) const {
  if (owner_thread == std::this_thread::get_id() && id < s.size()) [[likely]] return s[id];
  return get(id);
 }
 std::vector<W> s;
 std::vector<std::vector<W>> mem;
 virtual void evaluate() = 0;
 virtual void saveExtraState(std::vector<uint8_t>&) const {}
 virtual void restoreExtraState(const uint8_t*, size_t) {}
 virtual bool loadDerivedMemory(size_t, const std::string&, bool, size_t, size_t, size_t&) { return false; }
public:
 // clock is a scalar input index, or -1 for a clockless model. Initial evaluation is explicit.
 Model(std::string name, std::vector<Signal> signals, int clock,int word_abi=2);
 virtual ~Model();
 Model(const Model&) = delete; Model& operator=(const Model&) = delete;
 Model(Model&&) = delete; Model& operator=(Model&&) = delete;
 inline void eval() {
  if (fast_eval_ok && owner_thread == std::this_thread::get_id()) [[likely]] {
   fast_eval_ok = false;
   fast_evaluating = true;
   fast_dirty = false;
   try {
    evaluate();
   } catch (...) {
    fast_evaluating = false;
    fast_dirty = true;
    fast_eval_ok = fast_eligible;
    throw;
   }
   fast_evaluating = false;
   fast_eval_ok = fast_eligible;
   return;
  }
  evalSlow();
 }
 void final(); void timeInc(uint64_t delta);
 uint64_t time() const; bool gotFinish() const; bool evalNeeded() const; bool started() const;
 void requestEval();
 void finish(); void useVpi(); void loadVpi(const std::string& path);
 void openVcd(const std::string& path, const std::string& timescale = "1ns");
 void dumpVcd();
 void closeVcd();
 void saveCheckpoint(const std::string& path) const;
 void restoreCheckpoint(const std::string& path);
 size_t loadMemoryHex(size_t mem_id, const std::string& path, size_t start_addr = 0, size_t end_addr = SIZE_MAX);
 size_t loadMemoryBin(size_t mem_id, const std::string& path, size_t start_addr = 0, size_t end_addr = SIZE_MAX);
 void setDisplaySink(std::function<void(const std::string&)> sink);
 void emitDisplay(const std::string& text);
 virtual std::vector<CoveragePoint> coveragePoints() const { return {}; }
 void writeCoverageJson(const std::string& path) const;
 const std::string& name() const; const std::vector<Signal>& signals() const;
 W get(size_t id) const; void put(size_t id, W value);
 std::weak_ptr<int> lifetime() const;
 void checkThread() const;
 void notifyCallbackStateChanged(bool active) noexcept;
};
class Port {
 Model* model; size_t id; int width; bool is_input;
 static uint64_t toUint64Checked(const W& v) {
  uint64_t xz = 0;
  for (int i = 0; i < Bits::kLimbs; ++i) xz |= v.x.limb[i] | v.z.limb[i];
  if (xz != 0) throw std::runtime_error("integer conversion of X/Z port");
  uint64_t hi = 0;
  for (int i = 1; i < Bits::kLimbs; ++i) hi |= v.v.limb[i];
  if (hi != 0) throw std::overflow_error("port exceeds uint64_t");
  return v.v.limb[0];
 }
public:
 Port(Model& owner, size_t index)
  : model(&owner), id(index),
    width(index < owner.signals().size() ? owner.signals()[index].width : 0),
    is_input(index < owner.signals().size() && owner.signals()[index].direction == vpiInput) {}
 Port(const Port&) = delete; Port& operator=(const Port&) = delete;
 Port& operator=(uint64_t value) {
  if (model->canFastPortWrite() && is_input && width > 0) [[likely]] {
   if (width <= 64) [[likely]] {
    const uint64_t m = width == 64 ? ~uint64_t(0) : ((uint64_t(1) << width) - 1);
    W& dst = model->s[id];
    dst.v.limb[0] = value & m;
    dst.x.limb[0] = 0;
    dst.z.limb[0] = 0;
    model->markDirty();
    return *this;
   }
   model->s[id] = W{Bits(value), Bits(), Bits(), width};
   model->markDirty();
   return *this;
  }
  model->put(id, word(value, 0, 0, model->signals().at(id).width));
  return *this;
 }
 void bits(const std::string& value) {
  if (value.size() != size_t(width > 0 ? width : model->signals().at(id).width))
   throw std::invalid_argument("port binary width mismatch");
  model->put(id, parse(value));
 }
 std::string bits() const { return text(model->fastGet(id)); }
 W value() const { return model->fastGet(id); }
 explicit operator uint64_t() const {
  if (model->owner_thread == std::this_thread::get_id() && id < model->s.size()) [[likely]] {
   const W& v = model->s[id];
   if (v.w <= 64) [[likely]] {
    if ((v.x.limb[0] | v.z.limb[0]) != 0) throw std::runtime_error("integer conversion of X/Z port");
    return v.v.limb[0];
   }
   return toUint64Checked(v);
  }
  return toUint64Checked(model->get(id));
 }
};
}

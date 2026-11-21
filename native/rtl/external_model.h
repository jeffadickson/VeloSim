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
struct ToggleCoverage { const char* signal; int width; Bits rise; Bits fall; };
struct CoverBin { std::string name; uint64_t lo = 0; uint64_t hi = 0; bool is_default = false; bool is_ignore = false; bool is_illegal = false; uint64_t hits = 0; };
struct CoverpointSpec { std::string name; std::vector<CoverBin> bins; };
struct CrossSpec { std::string name; size_t cp_a = 0; size_t cp_b = 0; std::vector<uint64_t> hits; };
struct CovergroupSpec { std::string name; std::vector<CoverpointSpec> points; std::vector<CrossSpec> crosses; uint64_t samples = 0; };
struct PropertyStat { std::string name; std::string kind; uint64_t passes = 0; uint64_t failures = 0; uint64_t vacuous = 0; };
class Port;
class VS_VPI_API Model {
 friend class Port;
 struct State;
 std::unique_ptr<State> state;
protected:
 std::thread::id owner_thread;
 uintptr_t fast_thread_tag = 0;
 bool fast_eligible = false;
 bool fast_eval_ok = false;
 bool fast_port_ok = true;
 bool fast_evaluating = false;
 bool fast_dirty = true;
 static inline uintptr_t localThreadTag() noexcept {
  thread_local uint8_t tls_anchor = 0;
  return reinterpret_cast<uintptr_t>(&tls_anchor);
 }
 inline void initFastThreadTag() noexcept { fast_thread_tag = localThreadTag(); }
 inline bool isOwnerThreadFast() const noexcept {
  if (fast_thread_tag == localThreadTag()) [[likely]] return true;
  return owner_thread == std::this_thread::get_id();
 }
 void evalSlow();
 void applyForces() noexcept;
 void refreshFastFlags() noexcept;
 inline bool canFastPortWrite() const noexcept {
  return fast_port_ok && isOwnerThreadFast();
 }
 inline void markDirty() noexcept { fast_dirty = true; }
 inline W fastGet(size_t id) const {
  if (isOwnerThreadFast() && id < s.size()) [[likely]] {
   syncToBase();
   return s[id];
  }
  return get(id);
 }
 mutable std::vector<W> s;
 std::vector<std::vector<W>> mem;
 virtual void evaluate() = 0;
 virtual void syncToBase() const {}
 virtual void syncFromBase() {}
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
  if (fast_eval_ok && isOwnerThreadFast()) [[likely]] {
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
   if (!fast_eligible) [[unlikely]] applyForces();
   fast_eval_ok = fast_eligible;
   return;
  }
  evalSlow();
 }
 void final(); void timeInc(uint64_t delta);
 uint64_t time() const; bool gotFinish() const; int exitCode() const; bool evalNeeded() const; bool started() const;
 void requestEval();
 void finish(); void fatal(int code, const std::string& message = "");
 void useVpi(); void loadVpi(const std::string& path);
 void openVcd(const std::string& path, const std::string& timescale = "1ns",
              const std::vector<std::string>& filter_prefixes = {});
 void openVcdGz(const std::string& path, const std::string& timescale = "1ns",
                const std::vector<std::string>& filter_prefixes = {});
 void dumpVcd();
 void closeVcd();
 void saveCheckpoint(const std::string& path) const;
 void restoreCheckpoint(const std::string& path);
 size_t loadMemoryHex(size_t mem_id, const std::string& path, size_t start_addr = 0, size_t end_addr = SIZE_MAX);
 size_t loadMemoryBin(size_t mem_id, const std::string& path, size_t start_addr = 0, size_t end_addr = SIZE_MAX);
 virtual size_t memoryCount() const { return mem.size(); }
 virtual size_t memoryDepth(size_t mem_id) const { return mem_id < mem.size() ? mem[mem_id].size() : 0; }
 virtual int memoryWidth(size_t mem_id) const { return (mem_id < mem.size() && !mem[mem_id].empty()) ? mem[mem_id][0].w : 0; }
 virtual W readMemoryWord(size_t mem_id, size_t addr) const;
 virtual void writeMemoryWord(size_t mem_id, size_t addr, W value);
 void setDisplaySink(std::function<void(const std::string&)> sink);
 void emitDisplay(const std::string& text);
 void setPlusargs(std::vector<std::string> args);
 void addPlusarg(std::string arg);
 const std::vector<std::string>& plusargs() const;
 uint32_t testPlusargs(const std::string& prefix) const;
 uint32_t valuePlusargs(const std::string& spec, W& out) const;
 void setRandomSeed(uint64_t seed);
 uint64_t randomSeed() const;
 uint32_t nextUrandom();
 uint32_t nextUrandomRange(uint32_t max_val, uint32_t min_val = 0);
 int32_t nextRandom();
 void forceSignal(size_t id, W value);
 void releaseSignal(size_t id);
 bool isForced(size_t id) const;
 void enableToggleCoverage(bool enabled = true);
 std::vector<ToggleCoverage> toggleCoverage() const;
 size_t registerCovergroup(CovergroupSpec spec);
 void sampleCovergroup(size_t cg_id, const std::vector<uint64_t>& values, const std::vector<bool>& known);
 const std::vector<CovergroupSpec>& covergroups() const;
 size_t registerProperty(std::string name, std::string kind);
 void recordPropertyPass(size_t prop_id);
 void recordPropertyFail(size_t prop_id);
 void recordPropertyVacuous(size_t prop_id);
 const std::vector<PropertyStat>& propertyStats() const;
 virtual std::vector<CoveragePoint> coveragePoints() const { return {}; }
 void writeCoverageJson(const std::string& path) const;
 const std::string& name() const; const std::vector<Signal>& signals() const;
 W get(size_t id) const; void put(size_t id, W value);
 std::weak_ptr<int> lifetime() const;
 void checkThread() const;
 void notifyCallbackStateChanged(bool active) noexcept;
};
class Port {
 Model* const model; const size_t id; const int width; const bool is_input;
 const uint64_t mask64;
 uint64_t* const fast_v2;
 w64::W64* const fast_w64;
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
 Port(Model& owner, size_t index, uint64_t* v2_slot = nullptr, w64::W64* w64_slot = nullptr)
  : model(&owner), id(index),
    width(index < owner.signals().size() ? owner.signals()[index].width : 0),
    is_input(index < owner.signals().size() && owner.signals()[index].direction == vpiInput),
    mask64(width >= 64 ? ~uint64_t(0) : (width > 0 ? ((uint64_t(1) << width) - 1) : 0ULL)),
    fast_v2(v2_slot), fast_w64(w64_slot) {}
 Port(Model& owner, size_t index, w64::W64* w64_slot)
  : Port(owner, index, nullptr, w64_slot) {}
 Port(const Port&) = delete; Port& operator=(const Port&) = delete;
 inline Port& operator=(uint64_t value) {
  if (model->canFastPortWrite() && is_input) [[likely]] {
   if (fast_v2) {
    *fast_v2 = value & mask64;
    model->markDirty();
    return *this;
   }
   if (fast_w64) {
    fast_w64->v = value & mask64;
    fast_w64->x = 0;
    fast_w64->z = 0;
    model->markDirty();
    return *this;
   }
   if (width > 0 && width <= 64) {
    W& dst = model->s[id];
    dst.v.limb[0] = value & mask64;
    dst.x.limb[0] = 0;
    dst.z.limb[0] = 0;
    model->markDirty();
    return *this;
   }
   if (width > 64) {
    model->s[id] = W{Bits(value), Bits(), Bits(), width};
    model->markDirty();
    return *this;
   }
  }
  model->put(id, word(value, 0, 0, model->signals().at(id).width));
  return *this;
 }
 void bits(const std::string& value) {
  if (value.size() != size_t(width > 0 ? width : model->signals().at(id).width))
   throw std::invalid_argument("port binary width mismatch");
  model->put(id, parse(value));
 }
 std::string bits() const {
  if (model->isOwnerThreadFast()) [[likely]] {
   if (fast_v2) return text(w2::from(*fast_v2, width));
   if (fast_w64) return text(w64::from(*fast_w64));
  }
  return text(model->fastGet(id));
 }
 W value() const {
  if (model->isOwnerThreadFast()) [[likely]] {
   if (fast_v2) return w2::from(*fast_v2, width);
   if (fast_w64) return w64::from(*fast_w64);
  }
  return model->fastGet(id);
 }
 inline explicit operator uint64_t() const {
  if (model->isOwnerThreadFast()) [[likely]] {
   if (fast_v2) return *fast_v2;
   if (fast_w64) {
    if ((fast_w64->x | fast_w64->z) != 0) throw std::runtime_error("integer conversion of X/Z port");
    return fast_w64->v;
   }
   if (id < model->s.size()) [[likely]] {
    model->syncToBase();
    const W& v = model->s[id];
    if (v.w <= 64) [[likely]] {
     if ((v.x.limb[0] | v.z.limb[0]) != 0) throw std::runtime_error("integer conversion of X/Z port");
     return v.v.limb[0];
    }
    return toUint64Checked(v);
   }
  }
  return toUint64Checked(model->get(id));
 }
};
}

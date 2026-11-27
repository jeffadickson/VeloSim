#pragma once
#include "runtime.h"
#include <algorithm>
#include <deque>
#include <functional>
#include <iterator>
#include <map>
#include <optional>
#include <queue>
#include <unordered_map>
#include <variant>


namespace vsrtl {
/// Executing active/inactive/NBA kernel, separate from the legacy opaque-ID
/// transport. All simulation data is value-owned, making a public advance atomic.
/// Observed/reactive/postponed regions and races with multiple writers are not
/// modeled here. Executors must keep all mutable model state inside this kernel.
class HdlScheduler {
public:
    /// Sample a runtime integer delay once, before suspension. Invalid values
    /// fail transactionally instead of wrapping into a different event time.
    static uint64_t delayTicks(W amount, bool isSigned, uint64_t scale) {
        if(!scale || amount.x || amount.z || (isSigned && negative(amount)))
            throw std::invalid_argument("HDL delay must be known and nonnegative");
        if(amount.v>Bits(UINT64_MAX/scale)) throw std::overflow_error("HDL delay conversion overflow");
        return uint64_t(amount.v)*scale;
    }
    enum class Edge { change, posedge, negedge };
    enum class Join { all, any, none };
    // A memory watch observes any changed cell; indexes are resampled by the
    // resumed expression. Signal and memory identity spaces remain separate.
    struct Watch { size_t signal; Edge edge; bool memory=false; };
    struct Target { size_t signal; std::optional<size_t> index; };
    // A selected NBA stores replacement bits and their enable mask, never the
    // old destination word: unrelated writes before commit must survive.
    struct Write { Target target; W value; std::optional<Bits> enable={}; };
    struct Frame {
        size_t pc;
        bool finished = false, waiting = false, queued = false;

        std::vector<Watch> watches;
        std::vector<size_t> namedWatches;
        std::optional<Write> sampled;
        std::vector<Write> sampledWrites;
        // Instruction-keyed counters belong to a process, including forked
        // children, and survive delays without reevaluating repeat operands.
        std::map<size_t,unsigned> repeats;
        std::string source;
        std::optional<size_t> parent;
        std::vector<size_t> children, deferred, joining;
        Join join = Join::none;
    };
    struct Step {
        enum class Kind { again, delay, wait, done, join } kind;
        uint64_t ticks = 0;
        static Step again() { return {Kind::again}; }
        static Step delay(uint64_t ticks) { return {Kind::delay,ticks}; }
        static Step wait() { return {Kind::wait}; }
        static Step done() { return {Kind::done}; }
        static Step join() { return {Kind::join}; }
    };
    static constexpr size_t capacity = 20000;
private:
    using Nba = std::variant<Write,size_t>;
    struct Tick { std::deque<size_t> active; std::vector<Nba> nba; };
    struct SigWatcher { size_t frame; Edge edge; };
    struct MemUndo { size_t mem; size_t idx; W val; };

    std::vector<W> signals_;
    std::vector<std::vector<W>> memories_;
    // Fork appends frames while an executor holds its parent's reference.
    // deque preserves those references; vector reallocation would invalidate it.
    std::deque<Frame> frames_;
    // Indexed watcher tables eliminate O(N_frames) linear scans on signal/memory/event updates.
    std::vector<std::vector<SigWatcher>> signalWatchers_;
    std::vector<std::vector<size_t>> memWatchers_;
    std::vector<std::vector<size_t>> namedWatchers_;
    // O(1) hash-indexed future time slots paired with a lazy-deletion min-heap.
    std::unordered_map<uint64_t,Tick> futureSlots_;
    mutable std::priority_queue<uint64_t,std::vector<uint64_t>,std::greater<uint64_t>> futureHeap_;
    std::deque<size_t> active_, inactive_;
    std::vector<Nba> nba_;
    uint64_t now_ = 0;
    size_t remaining_ = capacity, pending_ = 0;
    size_t namedCount_ = 0;
    bool executing_ = false, started_ = false;

    // Reusable per-transaction undo journal: avoids full deep-copy of signals/memories/frames on happy paths
    // while guaranteeing exact atomic rollback if any exception is thrown.
    uint64_t undoSigMask_[4] = {};
    uint64_t undoFrameMask_[4] = {};
    size_t undoOrigFrameCount_ = 0;
    std::vector<std::pair<size_t,W>> undoSignals_;
    std::vector<MemUndo> undoMemories_;
    std::vector<std::pair<size_t,Frame>> undoFrames_;
    std::unordered_map<uint64_t,std::optional<Tick>> undoFuture_;
    std::deque<size_t> undoActive_, undoInactive_;
    std::vector<Nba> undoNba_;

    void touchSignal(size_t s) {
        if (!executing_) return;
        const size_t wordIdx = s >> 6;
        const uint64_t bit = uint64_t(1) << (s & 63);
        if (!(undoSigMask_[wordIdx] & bit)) {
            undoSigMask_[wordIdx] |= bit;
            undoSignals_.emplace_back(s, signals_[s]);
        }
    }
    void touchMemory(size_t m, size_t idx) {
        if (!executing_) return;
        undoMemories_.push_back({m, idx, memories_[m][idx]});
    }
    void touchFrame(size_t id) {
        if (!executing_ || id >= undoOrigFrameCount_) return;
        const size_t wordIdx = id >> 6;
        const uint64_t bit = uint64_t(1) << (id & 63);
        if (!(undoFrameMask_[wordIdx] & bit)) {
            undoFrameMask_[wordIdx] |= bit;
            undoFrames_.emplace_back(id, frames_[id]);
        }
    }
    void touchFuture(uint64_t t) {
        if (!executing_) return;
        if (undoFuture_.find(t) != undoFuture_.end()) return;
        if (auto it = futureSlots_.find(t); it != futureSlots_.end())
            undoFuture_.emplace(t, it->second);
        else
            undoFuture_.emplace(t, std::nullopt);
    }

    bool hasFuture() const {
        while (!futureHeap_.empty() && futureSlots_.find(futureHeap_.top()) == futureSlots_.end())
            futureHeap_.pop();
        return !futureHeap_.empty();
    }
    uint64_t earliestFuture() const { return futureHeap_.top(); }
    Tick& getOrCreateFutureSlot(uint64_t target) {
        touchFuture(target);
        auto [it, inserted] = futureSlots_.try_emplace(target);
        if (inserted) futureHeap_.push(target);
        return it->second;
    }
    void eraseFutureSlot(std::unordered_map<uint64_t,Tick>::iterator it) {
        futureSlots_.erase(it);
        if (futureSlots_.empty()) futureHeap_ = {};
    }
    void rebuildFutureHeap() {
        futureHeap_ = {};
        for (const auto& [t, _] : futureSlots_) futureHeap_.push(t);
    }

    void indexWatches(size_t id) {
        const auto& frame = frames_[id];
        for (const auto& w : frame.watches) {
            if (w.memory) memWatchers_[w.signal].push_back(id);
            else signalWatchers_[w.signal].push_back({id, w.edge});
        }
        for (size_t ev : frame.namedWatches) namedWatchers_[ev].push_back(id);
    }
    void unindexWatches(size_t id) {
        const auto& frame = frames_[id];
        for (const auto& w : frame.watches) {
            if (w.memory) {
                std::erase(memWatchers_[w.signal], id);
            } else {
                std::erase_if(signalWatchers_[w.signal], [id](const SigWatcher& sw) { return sw.frame == id; });
            }
        }
        for (size_t ev : frame.namedWatches)
            std::erase(namedWatchers_[ev], id);
    }
    void rebuildWatchIndexes() {
        for (auto& v : signalWatchers_) v.clear();
        for (auto& v : memWatchers_) v.clear();
        for (auto& v : namedWatchers_) v.clear();
        for (size_t id = 0; id < frames_.size(); ++id) {
            if (frames_[id].waiting && !frames_[id].finished) indexWatches(id);
        }
    }

    uint64_t futureTime(uint64_t ticks) const {
        if (ticks > UINT64_MAX - now_) throw std::overflow_error("HDL time overflow");
        return now_ + ticks;
    }
    void reserveEvent() {
        if (pending_ == capacity) throw std::length_error("HDL event capacity");
        ++pending_;
    }
    void activate(size_t id) {
        auto& frame = frames_.at(id);
        if (frame.finished || frame.queued) return;
        touchFrame(id);
        reserveEvent();
        active_.push_back(id);
        if (frame.waiting) unindexWatches(id);
        frame.queued = true;
        frame.waiting = false;
        frame.watches.clear();
        frame.namedWatches.clear();
    }
    void changed(size_t signal, W before, W after, bool memory=false) {
        if (exact(before,after)) return;
        size_t triggered[256];
        size_t count = 0;
        uint64_t seen[4] = {};
        if (memory) {
            const auto& watchers = memWatchers_[signal];
            if (watchers.empty()) return;
            for (size_t id : watchers) {
                const uint64_t bit = uint64_t(1) << (id & 63);
                if (!(seen[id >> 6] & bit)) {
                    seen[id >> 6] |= bit;
                    triggered[count++] = id;
                }
            }
        } else {
            const auto& watchers = signalWatchers_[signal];
            if (watchers.empty()) return;
            int posCache = -1, negCache = -1;
            for (const auto& w : watchers) {
                const uint64_t bit = uint64_t(1) << (w.frame & 63);
                if (seen[w.frame >> 6] & bit) continue;
                bool match = false;
                if (w.edge == Edge::change) match = true;
                else if (w.edge == Edge::posedge) {
                    if (posCache < 0) posCache = edge_event(before, after, true) ? 1 : 0;
                    match = posCache != 0;
                } else {
                    if (negCache < 0) negCache = edge_event(before, after, false) ? 1 : 0;
                    match = negCache != 0;
                }
                if (match) {
                    seen[w.frame >> 6] |= bit;
                    triggered[count++] = w.frame;
                }
            }
        }
        if (count > 1) std::sort(triggered, triggered + count);
        for (size_t i = 0; i < count; ++i) activate(triggered[i]);
    }
    void validate(const Write& write) const {
        if(write.enable && bool(*write.enable&~mask(write.value.w)))throw std::invalid_argument("HDL selected write mask");
        if(write.target.index) {
            if(write.target.signal>=memories_.size() || *write.target.index>=memories_[write.target.signal].size())
                throw std::out_of_range("HDL sampled memory address");
            if(write.value.w!=memories_[write.target.signal][*write.target.index].w) throw std::invalid_argument("HDL memory write width");
        } else {
            if(write.target.signal>=signals_.size()) throw std::out_of_range("HDL signal target");
            if(write.value.w!=signals_[write.target.signal].w) throw std::invalid_argument("HDL signal write width");
        }
    }
    static W merged(W before,const Write& write) {
        if(!write.enable)return write.value;
        const auto keep=~*write.enable;
        return word((before.v&keep)|(write.value.v&*write.enable),(before.x&keep)|(write.value.x&*write.enable),(before.z&keep)|(write.value.z&*write.enable),before.w);
    }
    void apply(const Write& write) {
        validate(write);
        if(write.target.index) {
            touchMemory(write.target.signal, *write.target.index);
            auto& cell=memories_[write.target.signal][*write.target.index];auto before=cell;cell=merged(before,write);
            changed(write.target.signal,before,cell,true);
        }
        else {
            touchSignal(write.target.signal);
            const auto old=signals_[write.target.signal]; signals_[write.target.signal]=merged(old,write);
            changed(write.target.signal,old,signals_[write.target.signal]);
        }
    }
    void consume() { if(!remaining_) throw std::runtime_error("HDL execution budget exceeded"); --remaining_; }
    void releaseChildren(size_t id) {
        auto deferred=std::move(frames_[id].deferred);frames_[id].deferred.clear();
        for(auto child:deferred) activate(child);
    }
    bool joinReady(const Frame& frame) const {
        if(frame.join==Join::none) return true;
        const auto done=[&](size_t child){return frames_.at(child).finished;};
        return frame.join==Join::all ? std::all_of(frame.joining.begin(),frame.joining.end(),done) :
               frame.joining.empty() || std::any_of(frame.joining.begin(),frame.joining.end(),done);
    }
    void complete(size_t id) {
        touchFrame(id);
        auto& frame=frames_[id];
        if (frame.waiting) unindexWatches(id);
        frame.finished=true;frame.waiting=false;frame.watches.clear();frame.namedWatches.clear();
        releaseChildren(id);
        if(frame.parent) {
            auto& parent=frames_[*frame.parent];
            if(parent.waiting && parent.join!=Join::none && joinReady(parent)) {
                touchFrame(*frame.parent);
                parent.join=Join::none;parent.joining.clear();activate(*frame.parent);
            }
        }
    }
    template<class Execute> void settle(Execute& execute) {
        while(!active_.empty() || !inactive_.empty() || !nba_.empty()) {
            if(active_.empty()) {
                if(!inactive_.empty()) active_.swap(inactive_);
                else {
                    // Apply the complete currently queued NBA batch before
                    // reentering active. Payload values/addresses were sampled
                    // when enqueued, never reevaluated during this commit.
                    auto batch=std::move(nba_); nba_.clear();
                    for(const auto& action:batch) {
                        consume();--pending_;
                        if(const auto* write=std::get_if<Write>(&action))apply(*write);
                        else trigger(std::get<size_t>(action));
                    }
                    continue;
                }
            }
            const size_t id=active_.front(); active_.pop_front(); --pending_;
            touchFrame(id);
            frames_.at(id).queued=false;
            if(frames_[id].finished) continue;
            while(true) {
              try {
                consume();
                const auto step=execute(id,frames_[id],*this);
                if(step.kind==Step::Kind::again) continue;
                if(step.kind==Step::Kind::done) { complete(id); break; }
                if(step.kind==Step::Kind::join) {
                    frames_[id].waiting=true;releaseChildren(id);
                    if(joinReady(frames_[id])) {frames_[id].join=Join::none;activate(id);}
                    break;
                }
                if(step.kind==Step::Kind::wait) {
                    frames_[id].waiting=true;
                    indexWatches(id);
                    releaseChildren(id);
                    break;
                }
                if(step.kind!=Step::Kind::delay) throw std::invalid_argument("HDL process step");
                const auto target=futureTime(step.ticks); reserveEvent();
                frames_[id].queued=true;
                if(!step.ticks) inactive_.push_back(id); else getOrCreateFutureSlot(target).active.push_back(id);
                releaseChildren(id);
                break;
              } catch(const std::exception& error) {
                // Handwritten executors without an origin retain their exact
                // exception type. Generated HDL supplies a last-source prefix.
                if(frames_[id].source.empty())throw;
                throw std::runtime_error(frames_[id].source+error.what());
              }
            }
        }
    }
    template<class Execute> void advance(uint64_t target, bool inclusive, Execute& execute) {
        if(target<now_) throw std::invalid_argument("HDL time cannot move backwards");
        if(now_<target || inclusive) settle(execute);
        while(hasFuture()) {
            const uint64_t nextT = earliestFuture();
            if (!(nextT < target || (inclusive && nextT == target))) break;
            futureHeap_.pop();
            touchFuture(nextT);
            auto it = futureSlots_.find(nextT);
            now_ = nextT;
            active_ = std::move(it->second.active);
            nba_ = std::move(it->second.nba);
            eraseFutureSlot(it);
            settle(execute);
        }
        now_=target;
    }
    template<class Action> void transaction(Action action) {
        if(executing_) throw std::logic_error("reentrant HDL scheduler advance");
        const uint64_t savedNow = now_;
        const size_t savedPending = pending_;
        const bool savedStarted = started_;
        undoOrigFrameCount_ = frames_.size();
        undoSigMask_[0] = undoSigMask_[1] = undoSigMask_[2] = undoSigMask_[3] = 0;
        undoFrameMask_[0] = undoFrameMask_[1] = undoFrameMask_[2] = undoFrameMask_[3] = 0;
        undoSignals_.clear();
        undoMemories_.clear();
        undoFrames_.clear();
        undoFuture_.clear();
        const bool hadActive = !active_.empty();
        const bool hadInactive = !inactive_.empty();
        const bool hadNba = !nba_.empty();
        if (hadActive) undoActive_ = active_; else undoActive_.clear();
        if (hadInactive) undoInactive_ = inactive_; else undoInactive_.clear();
        if (hadNba) undoNba_ = nba_; else undoNba_.clear();
        executing_ = true;
        remaining_ = capacity;
        try {
            action(*this);
            executing_ = false;
            started_ = true;
            undoSignals_.clear();
            undoMemories_.clear();
            undoFrames_.clear();
            undoFuture_.clear();
            if (hadActive) undoActive_.clear();
            if (hadInactive) undoInactive_.clear();
            if (hadNba) undoNba_.clear();
        } catch (...) {
            executing_ = false;
            now_ = savedNow;
            pending_ = savedPending;
            started_ = savedStarted;
            for (const auto& [s, val] : undoSignals_) signals_[s] = val;
            for (auto it = undoMemories_.rbegin(); it != undoMemories_.rend(); ++it)
                memories_[it->mem][it->idx] = it->val;
            frames_.resize(undoOrigFrameCount_);
            for (auto& [id, fr] : undoFrames_) frames_[id] = std::move(fr);
            for (auto& [t, optTick] : undoFuture_) {
                if (optTick) futureSlots_[t] = std::move(*optTick);
                else futureSlots_.erase(t);
            }
            active_ = std::move(undoActive_);
            inactive_ = std::move(undoInactive_);
            nba_ = std::move(undoNba_);
            undoSignals_.clear();
            undoMemories_.clear();
            undoFrames_.clear();
            undoFuture_.clear();
            rebuildFutureHeap();
            rebuildWatchIndexes();
            throw;
        }
    }
public:
    explicit HdlScheduler(std::vector<W> signals, std::vector<std::vector<W>> memories = {},size_t namedEvents=0)
        :signals_(std::move(signals)),memories_(std::move(memories)),
         signalWatchers_(signals_.size()),memWatchers_(memories_.size()),namedWatchers_(namedEvents),
         namedCount_(namedEvents) {
        if(signals_.empty() || signals_.size()>256 || memories_.size()>16 || namedCount_>256) throw std::invalid_argument("HDL storage shape");
        for(auto value:signals_) if(value.w<1 || value.w>256) throw std::invalid_argument("HDL signal width");
        for(const auto& memory:memories_) {
            if(memory.size()>256) throw std::invalid_argument("HDL memory capacity");
            for(auto value:memory) if(value.w<1 || value.w>256) throw std::invalid_argument("HDL memory width");
        }
    }
    uint64_t time() const { return now_; }
    W read(size_t signal) const { return signals_.at(signal); }
    W read(size_t memory,size_t index) const { return memories_.at(memory).at(index); }
    const std::vector<W>& signals() const { return signals_; }
    const std::vector<std::vector<W>>& memories() const { return memories_; }
    const std::deque<Frame>& frames() const { return frames_; }
    /// Process identities and PCs refer to immutable generated executor code.
    size_t addProcess(size_t entry) {
        if(executing_ || started_ || now_ || frames_.size()>=256) throw std::logic_error("add HDL processes before startup");
        const auto id=frames_.size(); frames_.push_back(Frame{entry}); activate(id); return id;
    }
    /// Register before returning wait(); value-level waits recheck their predicate
    /// in the executor after any watched change, then rearm if still false.
    void watch(Frame& frame,std::vector<Watch> watches,std::vector<size_t> named={}) {
        for(auto watch:watches) {
            if(watch.signal>=(watch.memory?memories_.size():signals_.size()) || int(watch.edge)<0 || watch.edge>Edge::negedge) throw std::invalid_argument("HDL sensitivity");
            if(watch.edge!=Edge::change && (watch.memory || signals_[watch.signal].w!=1)) throw std::invalid_argument("HDL edge requires scalar");
        }
        for(auto event:named)if(event>=namedCount_)throw std::out_of_range("HDL named event");
        if(watches.size()+named.size()>256) throw std::length_error("HDL sensitivity capacity");
        frame.watches=std::move(watches);frame.namedWatches=std::move(named);
    }
    /// Named triggers are occurrences, not latched booleans. A trigger before a
    /// process begins waiting is deliberately not remembered for that process.
    void trigger(size_t event) {
        if(!executing_ || event>=namedCount_) throw std::out_of_range("HDL named trigger");
        const auto& watchers = namedWatchers_[event];
        if (watchers.empty()) return;
        size_t triggered[256];
        size_t count = 0;
        uint64_t seen[4] = {};
        for (size_t id : watchers) {
            const uint64_t bit = uint64_t(1) << (id & 63);
            if (!(seen[id >> 6] & bit)) {
                seen[id >> 6] |= bit;
                triggered[count++] = id;
            }
        }
        if (count > 1) std::sort(triggered, triggered + count);
        for (size_t i = 0; i < count; ++i) activate(triggered[i]);
    }
    void blocking(Write write) { if(!executing_) throw std::logic_error("HDL writes require transaction"); apply(write); }
    /// Reuse the qualified packed-deposit rules to sample a selected address and
    /// payload. Unknown bases produce an empty mask; large bases retain the
    /// existing explicit range rejection. Two-state coercion precedes deposit.
    Write selected(size_t signal,W value,W base,int width,bool descending,bool twoState=false) const {
        if(!executing_)throw std::logic_error("HDL selected writes require transaction");
        const auto blank=word(0,0,0,signals_.at(signal).w);
        if(twoState)value=two_state(value);
        auto payload=deposit_indexed(blank,base,width,descending,value);
        auto enabled=deposit_indexed(blank,base,width,descending,word(mask(width),0,0,width)).v;
        return {{signal,std::nullopt},payload,enabled};
    }
    /// This bounded slice rejects unknown/out-of-range write addresses explicitly.
    /// General SV ignored-write behavior is a separate lowering acceptance item.
    static size_t sampledIndex(W index,int lower,size_t depth) {
        auto offset=memory_index(index,lower);
        if(offset.x || offset.z || offset.v>=Bits(depth)) throw std::out_of_range("HDL sampled memory address");
        return size_t(uint64_t(offset.v));
    }
    void nonblocking(Write write,uint64_t ticks=0) {
        if(!executing_) throw std::logic_error("HDL writes require transaction");
        validate(write); const auto target=futureTime(ticks); reserveEvent();
        if(!ticks) nba_.push_back(std::move(write)); else getOrCreateFutureSlot(target).nba.push_back(std::move(write));
    }
    /// A queued occurrence is delivered in the NBA region. A waiter armed after
    /// the trigger statement but before that region can therefore observe it.
    void nonblockingTrigger(size_t event,uint64_t ticks=0) {
        if(!executing_ || event>=namedCount_)throw std::out_of_range("HDL named trigger");
        const auto target=futureTime(ticks);reserveEvent();
        if(!ticks)nba_.push_back(event);else getOrCreateFutureSlot(target).nba.push_back(event);
    }
    /// join_none children become runnable only when the parent suspends/ends.
    /// Completed frames retain identity; the bounded slice caps total frames.
    Step fork(size_t parent,const std::vector<size_t>& entries,Join join) {
        if(!executing_ || int(join)<0 || join>Join::none) throw std::logic_error("invalid HDL fork");
        if(entries.size()>256-frames_.size()) throw std::length_error("HDL process frame capacity");
        touchFrame(parent);
        auto& owner=frames_.at(parent);owner.joining.clear();owner.join=join;
        if(entries.empty()){owner.join=Join::none;return Step::again();}
        for(auto entry:entries) {
            const size_t id=frames_.size();frames_.push_back(Frame{entry});frames_[id].parent=parent;
            owner.children.push_back(id);owner.deferred.push_back(id);owner.joining.push_back(id);
        }
        return join==Join::none ? Step::again() : Step::join();
    }
    Step waitFork(size_t parent) {
        if(!executing_) throw std::logic_error("HDL wait fork outside execution");
        touchFrame(parent);
        auto& owner=frames_.at(parent);owner.join=Join::all;owner.joining=owner.children;
        if(joinReady(owner)){owner.join=Join::none;owner.joining.clear();return Step::again();}
        return Step::join();
    }
    /// Cancel descendants, including suspended grandchildren, while retaining
    /// already sampled NBA updates. Those updates are separate scheduled work.
    void disableFork(size_t parent) {
        if(!executing_) throw std::logic_error("HDL disable fork outside execution");
        touchFrame(parent);
        std::vector<size_t> killed=frames_.at(parent).children;
        uint64_t killedMask[4] = {};
        for(size_t i=0;i<killed.size();++i) {
            const auto child=killed[i];
            killedMask[child >> 6] |= uint64_t(1) << (child & 63);
            for (size_t grand : frames_[child].children) killed.push_back(grand);
            touchFrame(child);
            if (frames_[child].waiting) unindexWatches(child);
            frames_[child].deferred.clear();frames_[child].finished=true;frames_[child].waiting=false;frames_[child].queued=false;frames_[child].watches.clear();frames_[child].namedWatches.clear();
        }
        const auto canceled=[&](size_t id){return (killedMask[id >> 6] & (uint64_t(1) << (id & 63))) != 0;};
        auto erase=[&](auto& queue){auto old=queue.size();std::erase_if(queue,canceled);pending_-=old-queue.size();};
        erase(active_);erase(inactive_);
        std::vector<uint64_t> toCheck;
        toCheck.reserve(futureSlots_.size());
        for (const auto& [t, _] : futureSlots_) toCheck.push_back(t);
        for (uint64_t t : toCheck) {
            auto it = futureSlots_.find(t);
            if (it == futureSlots_.end()) continue;
            bool anyCanceled = std::any_of(it->second.active.begin(), it->second.active.end(), canceled);
            if (!anyCanceled) continue;
            touchFuture(t);
            erase(it->second.active);
            if (it->second.active.empty() && it->second.nba.empty()) eraseFutureSlot(it);
        }
        frames_[parent].deferred.clear();
    }
    bool eventsPending() const { return pending_!=0; }
    std::optional<uint64_t> nextTimeSlot() const {
        if(!active_.empty() || !inactive_.empty() || !nba_.empty()) return now_;
        return hasFuture() ? std::optional<uint64_t>(earliestFuture()) : std::nullopt;
    }
    /// Atomically run through target, including all its supported regions.
    /// Each public advance gets a fresh bounded execution budget; callbacks may
    /// not reenter an advance or store mutable state outside this value object.
    template<class Execute> void runUntil(uint64_t target, Execute execute) {
        if (executing_) throw std::logic_error("reentrant HDL scheduler advance");
        if (target < now_) throw std::invalid_argument("HDL time cannot move backwards");
        if (active_.empty() && inactive_.empty() && nba_.empty() && (!hasFuture() || earliestFuture() > target)) {
            now_ = target;
            started_ = true;
            return;
        }
        transaction([&](auto& next){next.advance(target,true,execute);});
    }
    /// Process older events before applying future host inputs. Same-tick drives
    /// are an explicit host batch; no IEEE ordering is promised for design races.
    template<class Execute> void driveAt(uint64_t target,const std::vector<Write>& drives,Execute execute) {
        if (executing_) throw std::logic_error("reentrant HDL scheduler advance");
        if (target < now_) throw std::invalid_argument("HDL time cannot move backwards");
        for (const auto& write : drives) validate(write);
        transaction([&](auto& next){
            next.advance(target,false,execute);
            // Import the entire due slot before running host-triggered work.
            // Settling host reactivation first would commit its NBA updates
            // before already-scheduled active processes at this same tick.
            if(auto due=next.futureSlots_.find(target);due!=next.futureSlots_.end()){
                next.touchFuture(target);
                next.active_.insert(next.active_.end(),due->second.active.begin(),due->second.active.end());
                next.nba_.insert(next.nba_.end(),std::make_move_iterator(due->second.nba.begin()),std::make_move_iterator(due->second.nba.end()));
                next.eraseFutureSlot(due);
            }
            for(const auto& write:drives)next.apply(write);
            next.settle(execute);
        });
    }
};
}

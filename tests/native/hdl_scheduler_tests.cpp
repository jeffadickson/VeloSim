#include "../../native/rtl/hdl_scheduler.h"
#include <iostream>
using namespace vsrtl;
namespace {
using Step=HdlScheduler::Step;
using Frame=HdlScheduler::Frame;
void check(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
W value(uint64_t bits) {return word(bits,0,0,8);}
HdlScheduler::Write write(size_t signal,uint64_t bits) {return {{signal,std::nullopt},value(bits)};}
template<class Error,class F>void rejects(F action) {try{action();}catch(const Error&){return;}throw std::runtime_error("missing scheduler rejection");}
}
int main(){try{
    // #0 executes in inactive before NBA; a second #0 is still before the NBA.
    HdlScheduler order({value(0),value(0),value(0)});order.addProcess(0);
    auto regions=[](size_t,Frame& f,HdlScheduler& e){switch(f.pc++){
        case 0:e.nonblocking(write(0,7));return Step::delay(0);
        case 1:e.blocking({{1,std::nullopt},e.read(0)});return Step::delay(0);
        case 2:e.blocking({{2,std::nullopt},e.read(0)});return Step::done();
        default:throw std::runtime_error("bad PC");}};
    order.runUntil(0,regions);check(order.read(0).v==Bits(7)&&order.read(1).v==Bits(0)&&order.read(2).v==Bits(0),"inactive before NBA");
    check(!order.eventsPending()&&!order.nextTimeSlot(),"quiescence");

    // Two NBA updates form one batch. A change-sensitive process runs afterward
    // and sees both new values; it reenters active, and can itself enqueue NBA.
    HdlScheduler react({value(0),value(0),value(0)});react.addProcess(0);react.addProcess(10);
    auto reactivation=[](size_t,Frame& f,HdlScheduler& e){switch(f.pc){
        case 0:f.pc=1;e.watch(f,{{0,HdlScheduler::Edge::change}});return Step::wait();
        case 1:e.nonblocking({{2,std::nullopt},e.read(1)});return Step::done();
        case 10:e.nonblocking(write(0,1));e.nonblocking(write(1,42));return Step::done();
        default:throw std::runtime_error("bad PC");}};
    react.runUntil(0,reactivation);check(react.read(2).v==Bits(42),"NBA active fixpoint");

    // Sample delayed NBA payload/address now, even when each changes before t5.
    HdlScheduler sampled({value(0),value(3)},{{value(0),value(0)}});sampled.addProcess(0);
    auto samples=[](size_t,Frame& f,HdlScheduler& e){switch(f.pc++){
        case 0:e.nonblocking({{0,size_t(uint64_t(e.read(0).v))},e.read(1)},5);return Step::delay(1);
        case 1:e.blocking(write(0,1));e.blocking(write(1,99));return Step::done();
        default:throw std::runtime_error("bad PC");}};
    sampled.runUntil(1,samples);check(sampled.eventsPending()&&sampled.nextTimeSlot()==5,"pending query");
    sampled.runUntil(5,samples);check(sampled.read(0,0).v==Bits(3)&&sampled.read(0,1).v==Bits(0),"sampled delayed address/value");

    // Future host data cannot leak into a process due at an earlier time.
    HdlScheduler host({value(2),value(0),value(0)});host.addProcess(0);
    auto reader=[](size_t,Frame& f,HdlScheduler& e){switch(f.pc++){
        case 0:return Step::delay(3);
        case 1:e.blocking({{1,std::nullopt},e.read(0)});return Step::delay(4);
        case 2:e.blocking({{2,std::nullopt},e.read(0)});return Step::done();
        default:throw std::runtime_error("bad PC");}};
    host.driveAt(5,{write(0,9)},reader);host.runUntil(7,reader);
    check(host.read(1).v==Bits(2)&&host.read(2).v==Bits(9),"future host isolation");
    rejects<std::invalid_argument>([&]{host.runUntil(6,reader);});check(host.time()==7,"backward time rollback");

    // A wait(predicate) frame reevaluates/rearms at each relevant change.
    HdlScheduler wait({value(0),value(0)});wait.addProcess(0);
    auto predicate=[](size_t,Frame& f,HdlScheduler& e){
        if(e.read(0).v<Bits(3)){e.watch(f,{{0,HdlScheduler::Edge::change}});return Step::wait();}
        e.blocking(write(1,8));return Step::done();};
    wait.runUntil(0,predicate);wait.driveAt(1,{write(0,1)},predicate);
    check(wait.read(1).v==Bits(0)&&wait.frames()[0].waiting,"wait rearm");
    wait.driveAt(2,{write(0,3)},predicate);check(wait.read(1).v==Bits(8),"wait resume");

    // Scalar edges include the standard 0->X and X->1 positive transitions.
    HdlScheduler edges({word(0,0,0,1),value(0)});edges.addProcess(0);
    auto edge=[](size_t,Frame& f,HdlScheduler& e){if(f.pc++==0){e.watch(f,{{0,HdlScheduler::Edge::posedge}});return Step::wait();}e.blocking(write(1,1));return Step::done();};
    edges.runUntil(0,edge);edges.driveAt(1,{{{0,std::nullopt},unknown(1)}},edge);check(edges.read(1).v==Bits(1),"four-state edge");

    // All signals, memory, PCs, waits and future queues roll back on a failure.
    HdlScheduler atomic({value(0)});atomic.addProcess(0);
    auto fail=[](size_t,Frame& f,HdlScheduler& e){if(f.pc++==0){e.blocking(write(0,4));return Step::delay(2);}e.nonblocking(write(0,5),3);throw std::runtime_error("injected execution failure");return Step::done();};
    rejects<std::runtime_error>([&]{atomic.runUntil(3,fail);});
    check(atomic.time()==0&&atomic.read(0).v==Bits(0)&&atomic.frames()[0].pc==0&&atomic.nextTimeSlot()==0,"transaction rollback");
    auto done=[](size_t,Frame&,HdlScheduler&){return Step::done();};atomic.runUntil(3,done);check(atomic.time()==3&&!atomic.eventsPending(),"recovery after failure");
    HdlScheduler runaway({value(0)});runaway.addProcess(0);
    rejects<std::runtime_error>([&]{runaway.runUntil(0,[](size_t,Frame&,HdlScheduler&){return Step::again();});});
    check(runaway.time()==0&&runaway.frames()[0].pc==0&&runaway.eventsPending(),"budget rollback");
    HdlScheduler overflow({value(0)});overflow.addProcess(0);
    auto overflowExec=[](size_t,Frame& f,HdlScheduler&){return f.pc++?Step::delay(1):Step::delay(UINT64_MAX);};
    rejects<std::overflow_error>([&]{overflow.runUntil(UINT64_MAX,overflowExec);});check(overflow.time()==0&&overflow.nextTimeSlot()==0,"overflow rollback");
    rejects<std::logic_error>([&]{host.blocking(write(0,1));});
    rejects<std::logic_error>([&]{host.addProcess(0);});
    rejects<std::invalid_argument>([]{HdlScheduler invalid({});});
    HdlScheduler invalid({value(0)});invalid.addProcess(0);
    rejects<std::out_of_range>([&]{invalid.driveAt(0,{write(4,1)},done);});
    rejects<std::invalid_argument>([&]{invalid.driveAt(0,{{{0,std::nullopt},word(0,0,0,1)}},done);});
    for(auto mode:{HdlScheduler::Join::all,HdlScheduler::Join::any}) {
        HdlScheduler fork({value(0),value(0),value(0)});fork.addProcess(0);
        auto children=[mode](size_t id,Frame& f,HdlScheduler& e){switch(f.pc++){
            case 0:return e.fork(id,{10,20},mode);
            case 1:e.blocking(write(0,e.time()));if(mode==HdlScheduler::Join::any)e.disableFork(id);return Step::done();
            case 10:return Step::delay(2);case 11:e.blocking(write(1,2));return Step::done();
            case 20:return Step::delay(4);case 21:e.blocking(write(2,4));return Step::done();
            default:throw std::runtime_error("bad fork PC");}};
        fork.runUntil(5,children);
        check(fork.read(0).v==Bits(mode==HdlScheduler::Join::all?4:2),"join completion time");
        check(fork.read(1).v==Bits(2)&&fork.read(2).v==Bits(mode==HdlScheduler::Join::all?4:0)&&!fork.eventsPending(),"fork cancellation/pending events");
    }
    HdlScheduler detached({value(0),value(0),value(0)});detached.addProcess(0);
    auto joinNone=[](size_t id,Frame& f,HdlScheduler& e){switch(f.pc++){
        case 0:return e.fork(id,{10},HdlScheduler::Join::none);
        case 1:e.blocking(write(0,5));return e.waitFork(id);
        case 2:e.blocking(write(2,e.time()));return Step::done();
        case 10:e.blocking({{1,std::nullopt},e.read(0)});return Step::delay(2);
        case 11:return Step::done();default:throw std::runtime_error("bad join_none PC");}};
    detached.runUntil(3,joinNone);check(detached.read(1).v==Bits(5)&&detached.read(2).v==Bits(2),"join_none deferred start and wait fork");
    HdlScheduler nested({value(0)});nested.addProcess(0);
    auto descendants=[](size_t id,Frame& f,HdlScheduler& e){switch(f.pc++){
        case 0:return e.fork(id,{10},HdlScheduler::Join::none);
        case 1:return Step::delay(1);
        case 2:e.disableFork(id);return Step::done();
        case 10:return e.fork(id,{20},HdlScheduler::Join::none);
        case 11:return Step::done();
        case 20:e.nonblocking(write(0,3),3);return Step::delay(5);
        case 21:e.blocking(write(0,9));return Step::done();
        default:throw std::runtime_error("bad nested PC");}};
    nested.runUntil(6,descendants);check(nested.read(0).v==Bits(3)&&!nested.eventsPending(),"disable descendants retains sampled NBA");
    HdlScheduler named({value(0)},{},1);named.addProcess(0);named.addProcess(10);
    auto occurrences=[](size_t,Frame& f,HdlScheduler& e){switch(f.pc++){
        case 0:e.watch(f,{}, {0});return Step::wait();
        case 1:e.blocking(write(0,1));e.watch(f,{}, {0});return Step::wait();
        case 2:e.blocking(write(0,2));return Step::done();
        case 10:return Step::delay(1);
        case 11:e.trigger(0);return Step::delay(1);
        case 12:e.trigger(0);return Step::done();
        default:throw std::runtime_error("bad named event PC");}};
    named.runUntil(1,occurrences);check(named.read(0).v==Bits(1),"named event resume");
    named.runUntil(2,occurrences);check(named.read(0).v==Bits(2)&&!named.eventsPending(),"named event rearm");
    // Boundary/malformed API cases must reject atomically, not leave a partly
    // allocated queue or a parent whose PC has advanced past a failed fork.
    rejects<std::invalid_argument>([]{HdlScheduler invalid({W{0,0,0,0}});});
    rejects<std::invalid_argument>([]{HdlScheduler invalid({value(0)},std::vector<std::vector<W>>(17));});
    rejects<std::invalid_argument>([]{HdlScheduler invalid({value(0)},{{W{0,0,0,0}}});});
    rejects<std::invalid_argument>([]{HdlScheduler invalid({value(0)},{},257);});
    rejects<std::invalid_argument>([]{HdlScheduler invalid({value(0)},{std::vector<W>(257,value(0))});});
    HdlScheduler malformed({value(0)},{},1);malformed.addProcess(0);
    for(auto sensitivity:std::vector<std::vector<HdlScheduler::Watch>>{{{1,HdlScheduler::Edge::change}},{{0,static_cast<HdlScheduler::Edge>(99)}},{{0,static_cast<HdlScheduler::Edge>(-1)}},{{0,HdlScheduler::Edge::posedge}}})
        rejects<std::invalid_argument>([&]{malformed.runUntil(0,[&](size_t,Frame& f,HdlScheduler& e){e.watch(f,sensitivity);return Step::wait();});});
    rejects<std::length_error>([&]{malformed.runUntil(0,[](size_t,Frame& f,HdlScheduler& e){e.watch(f,std::vector<HdlScheduler::Watch>(257,{0,HdlScheduler::Edge::change}));return Step::wait();});});
    rejects<std::out_of_range>([&]{malformed.runUntil(0,[](size_t,Frame& f,HdlScheduler& e){e.watch(f,{}, {1});return Step::wait();});});
    rejects<std::out_of_range>([&]{malformed.runUntil(0,[](size_t,Frame&,HdlScheduler& e){e.trigger(1);return Step::done();});});
    rejects<std::invalid_argument>([&]{malformed.runUntil(0,[](size_t,Frame&,HdlScheduler&){return Step{static_cast<Step::Kind>(99)};});});
    rejects<std::logic_error>([&]{malformed.runUntil(0,[&](size_t,Frame&,HdlScheduler& e){e.runUntil(0,done);return Step::done();});});
    check(malformed.frames()[0].pc==0&&malformed.nextTimeSlot()==0,"malformed API rollback");
    malformed.runUntil(0,[](size_t,Frame&,HdlScheduler&){return Step::wait();});
    check(malformed.frames()[0].waiting&&!malformed.eventsPending(),"constant-false wait quiescence");
    rejects<std::logic_error>([&]{malformed.addProcess(0);});
    HdlScheduler frameLimit({value(0)});for(unsigned i=0;i<256;++i)frameLimit.addProcess(0);
    rejects<std::length_error>([&]{frameLimit.runUntil(0,[](size_t id,Frame&,HdlScheduler& e){return e.fork(id,{1},HdlScheduler::Join::all);});});
    check(frameLimit.frames().size()==256&&frameLimit.frames()[0].pc==0,"fork capacity rollback");
    HdlScheduler eventLimit({value(0)});eventLimit.addProcess(0);
    rejects<std::length_error>([&]{eventLimit.runUntil(0,[](size_t,Frame&,HdlScheduler& e){for(size_t i=0;i<=HdlScheduler::capacity;++i)e.nonblocking(write(0,1),1);return Step::done();});});
    check(eventLimit.read(0).v==Bits(0)&&eventLimit.nextTimeSlot()==0,"event capacity rollback");
    rejects<std::out_of_range>([]{HdlScheduler::sampledIndex(unknown(8),0,2);});
    rejects<std::out_of_range>([]{HdlScheduler::sampledIndex(value(2),0,2);});
    check(HdlScheduler::sampledIndex(value(3),2,2)==1,"sampled lower-bound conversion");
    HdlScheduler earlyTrigger({value(0)},{},1);earlyTrigger.addProcess(0);earlyTrigger.addProcess(10);
    earlyTrigger.runUntil(0,[](size_t,Frame& f,HdlScheduler& e){if(f.pc==0){e.trigger(0);return Step::done();}e.watch(f,{}, {0});return Step::wait();});
    check(earlyTrigger.frames()[1].waiting&&!earlyTrigger.eventsPending(),"named trigger is not latched");
    HdlScheduler largestTime({value(0)});largestTime.addProcess(0);
    largestTime.runUntil(UINT64_MAX,[](size_t,Frame& f,HdlScheduler&){return f.pc++?Step::done():Step::delay(UINT64_MAX);});
    check(largestTime.time()==UINT64_MAX&&!largestTime.eventsPending(),"largest representable delay is legal");
    HdlScheduler startupDrive({unknown(8),value(0)});startupDrive.addProcess(0);
    auto copyInput=[](size_t,Frame&,HdlScheduler& e){e.blocking({{1,std::nullopt},e.read(0)});return Step::done();};
    startupDrive.driveAt(0,{write(0,42)},copyInput);check(startupDrive.read(1).v==Bits(42),"host startup batch precedes initial reads");
    HdlScheduler sameTick({value(2),value(0)});sameTick.addProcess(0);
    auto boundaryRead=[](size_t,Frame& f,HdlScheduler& e){if(f.pc++==0)return Step::delay(3);e.blocking({{1,std::nullopt},e.read(0)});return Step::done();};
    sameTick.driveAt(3,{write(0,9)},boundaryRead);check(sameTick.read(1).v==Bits(9),"host batch precedes same-tick queued active work");
    HdlScheduler legalShapes(std::vector<W>(256,word(1,0,0,256)),std::vector<std::vector<W>>(16,std::vector<W>(256,word(0,0,0,256))),256);
    check(legalShapes.read(255).w==256&&legalShapes.read(15,255).w==256,"maximum legal storage shape");
    legalShapes.addProcess(0);legalShapes.runUntil(0,[](size_t,Frame& f,HdlScheduler& e){std::vector<HdlScheduler::Watch>w;for(size_t i=0;i<256;++i)w.push_back({i,HdlScheduler::Edge::change});e.watch(f,w);return Step::wait();});
    check(legalShapes.frames()[0].watches.size()==256,"maximum legal sensitivity");
    HdlScheduler minimumShapes({word(0,0,0,1)},{{word(0,0,0,1)}});check(minimumShapes.read(0,0).w==1,"one-bit memory cell");
    HdlScheduler negedge({word(1,0,0,1),value(0)});negedge.addProcess(0);
    auto negativeEdge=[](size_t,Frame& f,HdlScheduler& e){if(f.pc++==0){e.watch(f,{{0,HdlScheduler::Edge::negedge}});return Step::wait();}e.blocking(write(1,1));return Step::done();};
    negedge.runUntil(0,negativeEdge);negedge.driveAt(1,{{{0,std::nullopt},word(0,0,0,1)}},negativeEdge);check(negedge.read(1).v==Bits(1),"negative-edge sensitivity is legal");
    HdlScheduler exactBounds({value(0)},{{value(0),value(0)}});exactBounds.addProcess(0);
    rejects<std::out_of_range>([&]{exactBounds.driveAt(0,{write(1,1)},done);});
    rejects<std::out_of_range>([&]{exactBounds.driveAt(0,{{{1,size_t(0)},value(1)}},done);});
    rejects<std::out_of_range>([&]{exactBounds.driveAt(0,{{{0,size_t(2)},value(1)}},done);});
    rejects<std::logic_error>([&]{frameLimit.addProcess(0);});
    HdlScheduler fullFork({value(0)});fullFork.addProcess(0);
    fullFork.runUntil(0,[](size_t id,Frame& f,HdlScheduler& e){if(f.pc++==0)return e.fork(id,std::vector<size_t>(255,10),HdlScheduler::Join::all);return Step::done();});
    check(fullFork.frames().size()==256&&!fullFork.eventsPending(),"fork may fill the legal frame limit exactly");
    HdlScheduler partialCancellation({value(0),value(0)});partialCancellation.addProcess(0);partialCancellation.addProcess(30);
    auto partial=[](size_t id,Frame& f,HdlScheduler& e){switch(f.pc++){
        case 0:return e.fork(id,{10},HdlScheduler::Join::none);case 1:return Step::delay(1);case 2:e.disableFork(id);return Step::done();
        case 10:return Step::delay(5);case 11:e.blocking(write(0,9));return Step::done();
        case 30:return Step::delay(5);case 31:e.blocking(write(1,7));return Step::done();default:throw std::runtime_error("bad partial cancellation PC");}};
    partialCancellation.runUntil(6,partial);check(partialCancellation.read(0).v==Bits(0)&&partialCancellation.read(1).v==Bits(7)&&!partialCancellation.eventsPending(),"cancellation preserves unrelated same-tick work and queue accounting");
    check(HdlScheduler::delayTicks(value(3),false,10)==30,"runtime delay unit conversion");
    check(HdlScheduler::delayTicks(word(UINT64_MAX,0,0,64),false,1)==UINT64_MAX,"maximum runtime delay");
    check(HdlScheduler::delayTicks(value(0),true,10)==0,"zero signed runtime delay");
    rejects<std::invalid_argument>([]{HdlScheduler::delayTicks(unknown(8),false,1);});
    rejects<std::invalid_argument>([]{HdlScheduler::delayTicks(word(0,0,1,8),false,1);});
    rejects<std::invalid_argument>([]{HdlScheduler::delayTicks(value(255),true,1);});
    rejects<std::invalid_argument>([]{HdlScheduler::delayTicks(value(1),false,0);});
    rejects<std::overflow_error>([]{HdlScheduler::delayTicks(word(UINT64_MAX,0,0,64),false,2);});
    rejects<std::overflow_error>([]{HdlScheduler::delayTicks(word(UINT64_MAX/2+1,0,0,64),false,2);});
    rejects<std::overflow_error>([]{HdlScheduler::delayTicks(word(Bits(1)<<64,0,0,128),false,1);});
    HdlScheduler memoryWatch({value(0),value(0)},{{value(0),value(0)}});memoryWatch.addProcess(0);memoryWatch.addProcess(10);
    auto memoryExecutor=[](size_t,Frame& f,HdlScheduler& e){switch(f.pc++){
      case 0:e.watch(f,{{0,HdlScheduler::Edge::change,true}});return Step::wait();
      case 1:e.blocking(write(1,7));return Step::done();
      case 10:return Step::delay(1);
      case 11:e.blocking(write(0,1));return Step::delay(1);
      case 12:e.nonblocking({{0,size_t(1)},value(3)});return Step::done();
      default:throw std::runtime_error("invalid memory watch PC");}};
    memoryWatch.runUntil(1,memoryExecutor);check(memoryWatch.read(1).v==Bits(0),"signal ID cannot awaken same-numbered memory watch");
    memoryWatch.runUntil(2,memoryExecutor);check(memoryWatch.read(1).v==Bits(7),"memory NBA awakens memory sensitivity");
    HdlScheduler invalidMemoryWatch({value(0)},{{value(0)}});invalidMemoryWatch.addProcess(0);
    rejects<std::invalid_argument>([&]{invalidMemoryWatch.runUntil(0,[](size_t,Frame& f,HdlScheduler& e){e.watch(f,{{1,HdlScheduler::Edge::change,true}});return Step::wait();});});
    rejects<std::invalid_argument>([&]{invalidMemoryWatch.runUntil(0,[](size_t,Frame& f,HdlScheduler& e){e.watch(f,{{0,HdlScheduler::Edge::posedge,true}});return Step::wait();});});
    HdlScheduler nbaNamed({value(0)},{},1);nbaNamed.addProcess(0);
    nbaNamed.runUntil(0,[](size_t,Frame& f,HdlScheduler& e){if(f.pc++==0){e.nonblockingTrigger(0);e.watch(f,{}, {0});return Step::wait();}e.blocking(write(0,7));return Step::done();});
    check(nbaNamed.read(0).v==Bits(7)&&!nbaNamed.eventsPending(),"NBA occurrence reaches a later active waiter");
    HdlScheduler delayedNamed({value(0)},{},1);delayedNamed.addProcess(0);
    auto deferredTrigger=[](size_t,Frame& f,HdlScheduler& e){if(f.pc++==0){e.nonblockingTrigger(0,2);e.watch(f,{}, {0});return Step::wait();}e.blocking(write(0,9));return Step::done();};
    delayedNamed.runUntil(1,deferredTrigger);check(delayedNamed.read(0).v==Bits(0)&&delayedNamed.nextTimeSlot()==2,"delayed named occurrence remains pending");
    delayedNamed.runUntil(2,deferredTrigger);check(delayedNamed.read(0).v==Bits(9),"delayed named occurrence delivers at sampled time");
    rejects<std::out_of_range>([]{HdlScheduler e({value(0)},{},1);e.nonblockingTrigger(0);});
    HdlScheduler invalidQueuedTrigger({value(0)},{},1);invalidQueuedTrigger.addProcess(0);
    invalidQueuedTrigger.runUntil(0,[](size_t,Frame&,HdlScheduler& e){bool rejected=false;try{e.nonblockingTrigger(1);}catch(const std::out_of_range&){rejected=true;}check(rejected,"invalid named ID rejected before queue insertion");return Step::done();});
    HdlScheduler attributed({value(0)});attributed.addProcess(0);bool attributedFailure=false;
    try{attributed.runUntil(0,[](size_t,Frame& f,HdlScheduler& e){f.source="design.sv:7:3: ";e.blocking(write(0,8));return Step::delay(HdlScheduler::delayTicks(unknown(8),false,1));});}
    catch(const std::runtime_error& error){attributedFailure=std::string(error.what()).find("design.sv:7:3: HDL delay")!=std::string::npos;}
    check(attributedFailure&&attributed.read(0).v==Bits(0)&&attributed.frames()[0].source.empty(),"source-attributed failure preserves committed state");
    HdlScheduler selectedWrites({value(0)});selectedWrites.addProcess(0);
    selectedWrites.runUntil(0,[](size_t,Frame&,HdlScheduler& e){e.nonblocking(e.selected(0,value(3),value(0),2,false));e.nonblocking(e.selected(0,value(1),value(4),1,false));e.blocking(write(0,128));return Step::done();});
    check(selectedWrites.read(0).v==Bits(147),"selected NBAs merge with current unselected bits");
    HdlScheduler sampledSelection({value(0),value(0)});sampledSelection.addProcess(0);
    sampledSelection.runUntil(3,[](size_t,Frame& f,HdlScheduler& e){if(f.pc++==0){e.nonblocking(e.selected(0,value(3),e.read(1),2,false),3);return Step::delay(1);}e.blocking(write(0,128));e.blocking(write(1,4));return Step::done();});
    check(sampledSelection.read(0).v==Bits(131),"selected delayed NBA samples mask before index changes");
    HdlScheduler selectedStates({value(0),value(0)});selectedStates.addProcess(0);
    selectedStates.runUntil(0,[](size_t,Frame&,HdlScheduler& e){e.blocking(e.selected(0,parse("xz"),value(0),2,false));e.blocking(e.selected(1,parse("xz"),value(0),2,false,true));e.nonblocking(e.selected(0,value(0),unknown(8),2,false));return Step::done();});
    check(text(selectedStates.read(0))=="000000xz"&&selectedStates.read(1).v==Bits(0),"selected writes preserve four states, coerce two states and ignore unknown bases");
    HdlScheduler wideSelection({word(0,0,0,256)});wideSelection.addProcess(0);
    wideSelection.runUntil(0,[](size_t,Frame&,HdlScheduler& e){for(auto bit:{0,255})e.nonblocking(e.selected(0,word(1,0,0,1),word(bit,0,0,32),1,false));return Step::done();});
    check(text(wideSelection.read(0))=="1"+std::string(254,'0')+"1","selected NBA masks span all four limbs");
    HdlScheduler invalidMask({value(0)});invalidMask.addProcess(0);
    rejects<std::invalid_argument>([&]{invalidMask.runUntil(0,[](size_t,Frame&,HdlScheduler& e){e.blocking({{0,std::nullopt},value(0),Bits(256)});return Step::done();});});
    HdlScheduler hostBatch({value(0),value(0),value(0)});hostBatch.addProcess(0);hostBatch.addProcess(10);
    auto sameRegion=[](size_t,Frame& f,HdlScheduler& e){switch(f.pc++){
        case 0:e.watch(f,{{0,HdlScheduler::Edge::change}});return Step::wait();
        case 1:e.nonblocking(write(1,7));return Step::done();
        case 10:return Step::delay(3);
        case 11:e.blocking({{2,std::nullopt},e.read(1)});return Step::done();
        default:throw std::runtime_error("invalid host region PC");}};
    hostBatch.runUntil(0,sameRegion);hostBatch.driveAt(3,{write(0,1)},sameRegion);
    check(hostBatch.read(1).v==Bits(7)&&hostBatch.read(2).v==Bits(0),"host-triggered NBA waits for all due active processes");
    // Host deposits precede registration; only subsequent changes wake an
    // implicit waiter. Initial HDL writes and later host drives can do so.
    for(bool internal:{false,true}){
        HdlScheduler startup({value(0),value(0)});startup.addProcess(0);
        if(internal)startup.addProcess(10);
        auto implicit=[](size_t,Frame& f,HdlScheduler& e){switch(f.pc){
            case 0:e.watch(f,{{0,HdlScheduler::Edge::change}});f.pc=1;return Step::wait();
            case 1:e.blocking(write(1,uint64_t(e.read(1).v)+1));f.pc=0;return Step::again();
            case 10:e.blocking(write(0,3));return Step::done();
            default:throw std::runtime_error("invalid implicit sensitivity PC");}};
        startup.driveAt(0,{write(0,2)},implicit);
        check(startup.read(1).v==Bits(internal?1:0),"implicit waits for a change after registration");
        startup.driveAt(1,{write(0,9)},implicit);
        check(startup.read(1).v==Bits(internal?2:1),"implicit sensitivity rearms");
    }
    std::cout<<"HDL scheduler directed checks passed\n";
    return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}

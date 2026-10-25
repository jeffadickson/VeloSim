// Included inside rtl.cpp's private namespace, after expression/statement lowering.
struct FlatSignal{int width,direction;std::string path;bool is_signed;bool two_state;};
struct FlatMemory{int width,depth;bool is_signed;int lower;};
struct FlatScope{H handle,module;int parent;std::string path;std::map<std::string,std::string> names;};
struct FlatBinding{int parent,child,direction;H high,low;std::string target;};
std::vector<FlatScope> flat_scopes;
std::map<std::string,FlatSignal> flat_signals;
std::map<std::string,FlatMemory> flat_memories;
std::set<const void*> visited_scopes;
std::set<std::string> used_flat_names;
std::string scope_name(H h){auto local=name(h);if(!local.empty())return local;const char* raw=vpi_get_str(vpiFullName,h);std::string full=raw?raw:"";auto dot=full.rfind('.');need(dot!=std::string::npos&&dot+1<full.size(),"unnamed hierarchy scope");return full.substr(dot+1);}
void in_scope(int id){mod=flat_scopes.at(id).module;active_symbols=flat_scopes.at(id).names;}
int collect_scope(H scope,H module,int parent,const std::string& path,int depth=0){
 need(depth<32&&flat_scopes.size()<256,"hierarchy depth/scope budget exceeded");
 auto seen=visited_scopes.insert(identity(scope));need(seen.second,"duplicate elaborated scope");
 int id=int(flat_scopes.size());flat_scopes.push_back({scope,module,parent,path,{}});mod=module;
 for(int relation:{vpiPrimitive,vpiPrimitiveArray,vpiModuleArray})need(many(scope,relation).empty(),"primitives and module arrays unsupported");
 std::map<std::string,int> directions;
 if(parent<0)for(auto port:many(scope,vpiPort)){int direction=vpi_get(vpiDirection,port);need(direction==vpiInput||direction==vpiOutput,"inout unsupported");directions[name(port)]=direction;}
 for(int relation:{vpiNet,vpiReg,vpiVariables})for(auto h:many(scope,relation)){
  auto local=name(h);need(!local.empty(),"unnamed declaration");need(!one(h,vpiExpr),"declaration initialization unsupported");
  auto found=flat_scopes[id].names.find(local);std::string flat;
  if(found!=flat_scopes[id].names.end())flat=found->second;
  else{flat=path.empty()?local:"vs_h"+std::to_string(id)+"_"+local;auto base=flat;int suffix=0;while(used_flat_names.count(flat))flat=base+"_"+std::to_string(suffix++);used_flat_names.insert(flat);flat_scopes[id].names[local]=flat;}
  flat_symbols[identity(h)]=flat;
  if(type(h)==vpiArrayVar){auto ranges=many(h,vpiRange),elements=many(h,vpiReg);need(ranges.size()==1&&elements.size()==1,"only one-dimensional unpacked word arrays supported");need(!one(elements[0],vpiExpr),"initialized memory elements unsupported");need(type(elements[0])!=vpiBitVar&&type(one(one(elements[0],vpiTypespec),vpiActual))!=vpiBitTypespec,"two-state memories unsupported");logic_word(elements[0]);auto lo=integer(one(ranges[0],vpiLeftRange)),hi=integer(one(ranges[0],vpiRightRange));auto lower=std::min(lo,hi),upper=std::max(lo,hi);need(upper<=INT32_MAX&&upper-lower<256,"memory requires nonnegative bounds and 1..256 words");flat_memories[flat]={width(elements[0]),int(upper-lower+1),declared_signed(elements[0]),int(lower)};}
  else{logic_word(h);if(relation==vpiNet){auto nettype=vpi_get(vpiNetType,h);need(nettype==vpiWire||nettype==vpiLogicNet,"resolved/pull net types unsupported");}int w=width(h);auto [old,inserted]=flat_signals.emplace(flat,FlatSignal{w,parent<0?directions[local]:0,path.empty()?"":path+"."+local,declared_signed(h),type(h)==vpiBitVar||type(one(one(h,vpiTypespec),vpiActual))==vpiBitTypespec});need(inserted||old->second.width==w,"conflicting signal widths");}
 }
 need(flat_signals.size()<=256&&flat_memories.size()<=16,"flattened declaration budget exceeded");
 for(auto child:many(scope,vpiModule))collect_scope(child,child,id,path.empty()?scope_name(child):path+"."+scope_name(child),depth+1);
 for(auto array:many(scope,vpiGenScopeArray))for(auto child:many(array,vpiGenScope))if(!visited_scopes.count(identity(child)))collect_scope(child,module,id,path.empty()?scope_name(child):path+"."+scope_name(child),depth+1);
 for(auto child:many(scope,vpiGenScope))if(!visited_scopes.count(identity(child)))collect_scope(child,module,id,path.empty()?scope_name(child):path+"."+scope_name(child),depth+1);
 return id;
}
bool declared_reference(H h){return type(h)==vpiRefObj||type(h)==vpiReg||type(h)==vpiNet||type(h)==vpiBitVar||type(h)==vpiVariables;}
struct FlatProcess{int scope;H body;std::vector<std::pair<std::string,bool>> events;};
void input_events(H edge,int scope,const std::map<std::string,std::string>& links,std::vector<std::pair<std::string,bool>>& events,int depth=0){
 need(edge&&depth<16&&type(edge)==vpiOperation,"explicit edge event required");int operation=vpi_get(vpiOpType,edge);auto operands=many(edge,vpiOperand);
 if(operation==vpiEventOrOp){for(auto operand:operands)input_events(operand,scope,links,events,depth+1);return;}
 need(operation==vpiPosedgeOp||operation==vpiNegedgeOp,"only input posedge/negedge events supported");need(operands.size()==1&&declared_reference(operands[0]),"one declared event input required");in_scope(scope);auto root=signal_name(operands[0]);std::vector<std::string> chain;
 while(links.count(root)){need(chain.size()<256&&std::find(chain.begin(),chain.end(),root)==chain.end(),"cyclic event port connection");chain.push_back(root);root=links.at(root);}
 need(flat_signals.count(root)&&flat_signals.at(root).width==1&&flat_signals.at(root).direction==vpiInput,"event controls must connect directly to a top scalar input");
 for(auto& alias:chain){need(flat_signals.at(alias).width==1&&flat_signals.at(alias).is_signed==flat_signals.at(root).is_signed&&flat_signals.at(alias).two_state==flat_signals.at(root).two_state,"event aliases require matching signedness");clock_aliases[alias]=root;}
 auto event=std::make_pair(root,operation==vpiPosedgeOp);need(events.size()<16&&std::find(events.begin(),events.end(),event)==events.end(),"duplicate or excessive process events");events.push_back(event);
}
std::string flatten_design(H top){
 collect_scope(top,top,-1,"");std::vector<FlatBinding> bindings;std::map<std::string,std::string> input_links;
 for(size_t index=1;index<flat_scopes.size();++index){auto scope=flat_scopes[index];if(type(scope.handle)!=vpiModule)continue;
  for(auto port:many(scope.handle,vpiPort)){auto high=one(port,vpiHighConn),low=one(port,vpiLowConn);int direction=vpi_get(vpiDirection,port);need(high&&low,"open/unresolved instance ports unsupported");need(direction==vpiInput||direction==vpiOutput,"inout instance ports unsupported");need(declared_reference(low),"instance low connection must be a declared word");in_scope(int(index));auto child=signal_name(low);need(flat_signals.count(child),"memory instance ports unsupported");int childWidth=flat_signals.at(child).width;in_scope(scope.parent);need(width(high)>=1&&childWidth>=1,"instance connections require word widths");if(direction==vpiOutput)need(declared_reference(high),"instance output requires a whole declared destination word");bindings.push_back({scope.parent,int(index),direction,high,low,child});if(direction==vpiInput&&declared_reference(high))input_links[child]=signal_name(high);}
 }
 std::vector<std::pair<int,H>> combinational;std::vector<FlatProcess> processes;std::string clock;bool explicit_events=false;
 for(size_t index=0;index<flat_scopes.size();++index)for(auto process:many(flat_scopes[index].handle,vpiProcess)){
  if(type(process)==vpiAlways&&vpi_get(vpiAlwaysType,process)==vpiAlwaysComb){need(combinational.size()<256,"combinational process budget exceeded");combinational.push_back({int(index),one(process,vpiStmt)});continue;}
  need(processes.size()<256,"process budget exceeded");need(type(process)==vpiAlways&&vpi_get(vpiAlwaysType,process)!=vpiAlwaysComb&&vpi_get(vpiAlwaysType,process)!=vpiAlwaysLatch,"only explicitly edge-controlled always processes supported");auto event=one(process,vpiStmt);need(type(event)==vpiEventControl,"event control required");FlatProcess lowered{int(index),one(event,vpiStmt),{}};input_events(one(event,vpiCondition),int(index),input_links,lowered.events);need(!lowered.events.empty(),"empty event list");auto primary=lowered.events.front().first;if(clock.empty())clock=primary;explicit_events|=lowered.events.size()!=1||!lowered.events.front().second||primary!=clock;processes.push_back(std::move(lowered));
 }
 need(!processes.empty(),"at least one sequential process required");
 std::string out="{\"format\":\"velosim.rtl.v1\",\"signals\":[";bool first=true;
 for(auto& [flat,signal]:flat_signals){if(clock_aliases.count(flat))continue;if(!first)out+=",";first=false;out+="{\"name\":"+q(flat)+",\"width\":"+std::to_string(signal.width)+",\"direction\":"+q(signal.direction==vpiInput?"input":signal.direction==vpiOutput?"output":"internal");if(signal.is_signed)out+=",\"signed\":true";if(signal.two_state)out+=",\"two_state\":true";if(!signal.path.empty())out+=",\"path\":"+q(signal.path);out+="}";}
 out+="],\"memories\":[";first=true;for(auto& [flat,memory]:flat_memories){if(!first)out+=",";first=false;out+="{\"name\":"+q(flat)+",\"width\":"+std::to_string(memory.width)+",\"depth\":"+std::to_string(memory.depth)+(memory.lower?",\"lower\":"+std::to_string(memory.lower):"")+(memory.is_signed?",\"signed\":true":"")+"}";}
 out+="],\"continuous\":[";first=true;auto append=[&](const std::string& target,const std::string& value){if(!first)out+=",";first=false;out+="{\"target\":"+target+",\"value\":"+value+"}";};
 for(auto& binding:bindings){if(clock_aliases.count(binding.target))continue;in_scope(binding.parent);auto high=expr(binding.high);in_scope(binding.child);auto low=expr(binding.low);if(binding.direction==vpiInput)append(low,high);else append(high,low);}
 for(size_t index=0;index<flat_scopes.size();++index){in_scope(int(index));for(auto assignment:many(flat_scopes[index].handle,vpiContAssign)){auto object=static_cast<const UHDM::cont_assign*>(identity(assignment));need(!object->Delay()&&!object->VpiStrength0()&&!object->VpiStrength1(),"continuous delays and strengths unsupported");append(expr(one(assignment,vpiLhs)),expr(one(assignment,vpiRhs)));}}
 out+="],\"combinational\":[";first=true;for(auto& [scope,body]:combinational){in_scope(scope);if(!first)out+=",";first=false;out+=stmt(body);}out+="],\"clock\":"+q(clock)+",\"body\":";if(processes.size()>1)out+="{\"kind\":\"parallel\",\"items\":[";first=true;
 for(auto& process:processes){in_scope(process.scope);if(!first)out+=",";first=false;if(explicit_events){out+="{\"kind\":\"process\",\"events\":[";bool first_event=true;for(auto& [signal,positive]:process.events){if(!first_event)out+=",";first_event=false;out+="{\"signal\":"+q(signal)+",\"edge\":"+q(positive?"posedge":"negedge")+"}";}out+="],\"body\":";}out+=stmt(process.body);if(explicit_events)out+="}";}if(processes.size()>1)out+="]}";return out+"}\n";
}

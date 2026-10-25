// Pure automatic subroutines are interpreted structurally into expression trees.
// Every formal/local write retains its declared sizing barrier; no HDL text is substituted.
struct LocalWord{H declaration;std::string value;bool assigned=false;};
std::map<std::string,LocalWord> call_locals;
bool inside_call=false;
std::set<const void*> active_calls;
std::string expr(H,int);
std::string lower_function(H,int);
std::string lower_task(H,int);
H callee(H call,bool function){auto h=one(call,function?vpiFunction:vpiTask);need(h,"unresolved subroutine call");return h;}
H formal_word(H h){auto binding=one(h,vpiExpr);return binding?binding:h;}
// UHDM may clone the declaration referenced by an elaborated begin block.
// Accept that alias only with exact scope AND lexical declaration coordinates.
bool same_local(H a,H b){
 if(identity(a)==identity(b))return true;if(!a||!b||type(a)!=type(b))return false;
 auto left=static_cast<const UHDM::BaseClass*>(identity(a)),right=static_cast<const UHDM::BaseClass*>(identity(b));
 const char* raw=vpi_get_str(vpiFullName,a);std::string full=raw?raw:"";raw=vpi_get_str(vpiFullName,b);std::string other=raw?raw:"";
 return !full.empty()&&full==other&&left->VpiFile()==right->VpiFile()&&left->VpiLineNo()!=0&&left->VpiLineNo()==right->VpiLineNo()&&left->VpiColumnNo()==right->VpiColumnNo();
}
std::string local_key(H h){
 for(int i=0;h&&i<64;i++){for(auto& [key,word]:call_locals)if(same_local(h,word.declaration))return key;auto actual=one(h,vpiActual);if(!actual&&type(h)==vpiIODecl)actual=one(h,vpiExpr);if(identity(actual)==identity(h))break;h=actual;}
 return "";
}
std::string convert_word(H declaration,const std::string& value){output_budget(value);for(int i=0;i<64;i++){auto actual=one(declaration,vpiActual);if(!actual||identity(actual)==identity(declaration))break;declaration=actual;}if(type(word_spec(declaration))!=vpiIntTypespec)logic_word(declaration);auto spec=word_spec(declaration);return "{\"kind\":\"convert\",\"value\":"+value+",\"width\":"+std::to_string(width(declaration))+",\"signed\":"+(declared_signed(declaration)?"true":"false")+",\"two_state\":"+(two_state_word(declaration)?"true":"false")+"}";}
H current_function=nullptr;
H parameter_owner(H declaration){
 std::set<const void*> seen;H current=one(declaration,vpiParent);
 for(unsigned depth=0;current&&depth<64;depth++){
  need(seen.insert(identity(current)).second,"cyclic parameter owner chain");int t=type(current);if(t==vpiFunction||t==vpiTask||t==vpiModule||t==vpiGenScope)return current;current=one(current,vpiParent);
 }
 throw std::runtime_error("parameter declaration has no bounded lexical owner");
}
bool owned_parameter(H declaration,H scope){auto owner=parameter_owner(declaration);return identity(owner)==identity(scope)||same_local(owner,scope);}
// Recover declaration bindings from the original typed UHDM graph when elaboration
// rewires a shadowed name incorrectly. Exact lexical coordinates identify syntax;
// the visible elaborated scope still supplies the value and instance override.
std::string lexical_key(H h){auto key=expression_location(h);for(int i=0;i<2&&!key.empty();i++)key=key.substr(0,key.rfind(':'));return key;}
std::string owner_definition(H scope){std::set<const void*> seen;for(unsigned i=0;scope&&i<64;i++){need(seen.insert(identity(scope)).second,"cyclic definition scope");if(type(scope)==vpiModule){const char* value=vpi_get_str(vpiDefName,scope);return value?value:"";}scope=one(scope,vpiParent);}return "";}
std::string canonical_key(H declaration){auto owner=parameter_owner(declaration);return lexical_key(declaration)+"|"+std::to_string(type(owner))+"|"+(type(owner)==vpiModule?owner_definition(owner):lexical_key(owner)+"|"+owner_definition(owner));}
std::map<std::string,H> syntax_parameters,syntax_assignments,syntax_references,syntax_parameter_values;
std::set<const void*> syntax_visited;
void syntax_binding(std::map<std::string,H>& entries,H syntax,H declaration){auto key=lexical_key(syntax);if(key.empty())return;auto [it,added]=entries.emplace(key,declaration);if(!added&&(!it->second||canonical_key(it->second)!=canonical_key(declaration)))it->second=nullptr;}
std::set<std::string> syntax_nonparameters,syntax_parameter_selections;
size_t syntax_symbol_checks=0;
H original_lexical_declaration(H reference){
 auto symbol=name(reference);if(symbol.empty())return nullptr;std::set<const void*> seen;H scope=one(reference,vpiParent);
 for(unsigned depth=0;scope&&depth<128;depth++){
  need(seen.insert(identity(scope)).second,"cyclic original lexical scope");H found=nullptr;
  auto consider=[&](H declaration){need(++syntax_symbol_checks<=1000000,"original symbol resolution budget exceeded");if(name(declaration)!=symbol)return;if(type(declaration)==vpiParameter&&!owned_parameter(declaration,scope))return;if(found&&identity(found)!=identity(declaration))need(same_local(found,declaration),"ambiguous original lexical symbol");else found=declaration;};
  for(int relation:{vpiParameter,vpiVariables,vpiReg,vpiNet,vpiIODecl})for(auto declaration:many(scope,relation))consider(declaration);
  for(auto assignment:many(scope,vpiParamAssign)){auto declaration=one(assignment,vpiLhs);if(type(declaration)==vpiParameter&&owned_parameter(declaration,scope))consider(declaration);}
  if(found)return found;scope=one(scope,vpiParent);
 }
 return nullptr;
}
bool is_parameter_reference(H h){
 if(type(h)!=vpiRefObj&&type(h)!=vpiRefVar)return false;auto key=lexical_key(h);auto original=syntax_references.find(key);if(original!=syntax_references.end()){need(!syntax_nonparameters.count(key),"ambiguous original parameter/variable reference");need(original->second,"ambiguous original parameter reference");return true;}
 if(syntax_nonparameters.count(key)){need(type(one(h,vpiActual))!=vpiParameter,"elaborated parameter conflicts with original lexical variable");return false;}
 return type(one(h,vpiActual))==vpiParameter;
}
void collect_parameter_syntax(H h,int depth=0){
 if(!h||!syntax_visited.insert(identity(h)).second)return;need(depth<128&&syntax_visited.size()<=100000,"parameter syntax budget exceeded");
 if(type(h)==vpiBitSelect||type(h)==vpiPartSelect||type(h)==vpiIndexedPartSelect){
  auto base=one(h,vpiActual);auto declaration=type(base)==vpiParameter?base:original_lexical_declaration(name(h).empty()?base:h);
  if(type(declaration)==vpiParameter)syntax_parameter_selections.insert(lexical_key(h));
 }
 if(type(h)==vpiParameter)syntax_binding(syntax_parameters,h,h);
 if(type(h)==vpiParamAssign&&type(one(h,vpiLhs))==vpiParameter){syntax_binding(syntax_assignments,h,one(h,vpiLhs));auto key=lexical_key(h);auto rhs=one(h,vpiRhs);auto [entry,added]=syntax_parameter_values.emplace(key,rhs);if(!added&&expression_location(entry->second)!=expression_location(rhs))entry->second=nullptr;}
 if(type(h)==vpiRefObj||type(h)==vpiRefVar){auto declaration=original_lexical_declaration(h);if(type(declaration)==vpiParameter)syntax_binding(syntax_references,h,declaration);else if(declaration)syntax_nonparameters.insert(lexical_key(h));}
 for(int relation:{vpiStmt,vpiCondition,vpiElseStmt,vpiLhs,vpiRhs,vpiReturn,vpiForInitStmt,vpiForIncStmt,vpiHighConn,vpiLowConn,vpiExpr})collect_parameter_syntax(one(h,relation),depth+1);
 for(int relation:{vpiModule,vpiTaskFunc,vpiPort,vpiVariables,vpiReg,vpiNet,vpiParameter,vpiParamAssign,vpiProcess,vpiContAssign,vpiStmt,vpiOperand,vpiArgument,vpiGenScopeArray,vpiGenScope})for(auto child:many(h,relation))collect_parameter_syntax(child,depth+1);
}
H canonical_parameter(H declaration){auto found=syntax_parameters.find(lexical_key(declaration));if(found==syntax_parameters.end())return declaration;need(found->second,"ambiguous original parameter declaration");return found->second;}
H parameter_context=nullptr;
std::vector<H> parameter_visible_scopes;
struct ParameterBinding{H declaration,rhs,scope,context,canonical;};
std::vector<ParameterBinding> parameter_bindings;
bool parameter_definition(H owner,H scope){
 if(type(owner)!=type(scope))return false;
 if(type(scope)==vpiModule){const char* left=vpi_get_str(vpiDefName,owner);std::string definition=left?left:"";const char* right=vpi_get_str(vpiDefName,scope);return !definition.empty()&&right&&definition==right;}
 return lexical_key(owner)==lexical_key(scope)&&owner_definition(owner)==owner_definition(scope);
}
void bind_parameter(H declaration,H rhs,H scope,H canonical){
 need(!lexical_key(canonical).empty(),"parameter requires lexical declaration coordinates");auto owner=parameter_owner(canonical);if(!parameter_definition(owner,scope))return;
 declaration=canonical; // Preserve original declaration types when elaboration corrupts a shadowed typespec.
 H context=(type(scope)==vpiFunction||type(scope)==vpiTask)?parameter_context:scope;
 need(context,"parameter requires an elaborated instance context");
 auto prior=std::find_if(parameter_bindings.begin(),parameter_bindings.end(),[&](const auto& p){return identity(p.context)==identity(context)&&identity(p.scope)==identity(scope)&&canonical_key(p.canonical)==canonical_key(canonical);});
 if(prior==parameter_bindings.end()){parameter_bindings.push_back({declaration,rhs,scope,context,canonical});return;}
 if(!rhs)return;if(!prior->rhs){prior->rhs=rhs;return;}if(identity(prior->rhs)==identity(rhs))return;
 need(type(prior->rhs)==vpiConstant&&type(rhs)==vpiConstant,"conflicting parameter declaration bindings");int w=type(word_spec(declaration))==vpiIntTypespec?32:width(declaration);
 auto normalized=[&](H value){auto bitsValue=bits(value);if(bitsValue.size()>size_t(w))return bitsValue.substr(bitsValue.size()-w);return std::string(w-bitsValue.size(),signed_expression(value,0)&&bitsValue[0]=='1'?'1':'0')+bitsValue;};need(normalized(prior->rhs)==normalized(rhs),"conflicting parameter declaration bindings");
}
void bind_parameters(H scope){
 auto declarations=many(scope,vpiParameter);
 for(auto assignment:many(scope,vpiParamAssign)){
  auto declaration=one(assignment,vpiLhs);need(type(declaration)==vpiParameter,"parameter binding requires declaration identity");H canonical=canonical_parameter(declaration);
  auto original=syntax_assignments.find(lexical_key(assignment));if(original!=syntax_assignments.end()){need(original->second,"ambiguous original parameter assignment");canonical=original->second;}
  if(!parameter_definition(parameter_owner(canonical),scope))continue;
  // Prefer the elaborated declaration with matching canonical syntax coordinates.
  auto match=std::find_if(declarations.begin(),declarations.end(),[&](H item){return lexical_key(item)==lexical_key(canonical);});
  if(match!=declarations.end())declaration=*match;else if(lexical_key(declaration)!=lexical_key(canonical))declaration=canonical;
  auto rhs=one(assignment,vpiRhs);auto sourceValue=syntax_parameter_values.find(lexical_key(assignment));
  if(!static_cast<const UHDM::param_assign*>(identity(assignment))->VpiOverriden()&&sourceValue!=syntax_parameter_values.end()){need(sourceValue->second,"ambiguous original parameter value");rhs=sourceValue->second;}
  else if(type(rhs)==vpiConstant){auto originalValue=original_expression(rhs);if(type(originalValue)==vpiOperation)rhs=originalValue;}
  bind_parameter(declaration,rhs,scope,canonical);
 }
 for(auto declaration:declarations)bind_parameter(declaration,nullptr,scope,canonical_parameter(declaration));
}
const ParameterBinding* find_parameter_binding(H reference){
 H parameter=type(reference)==vpiParameter?reference:one(reference,vpiActual);H canonical=nullptr;auto original=syntax_references.find(lexical_key(reference));
 if(original!=syntax_references.end()){need(original->second,"ambiguous original parameter reference");canonical=original->second;}else{need(type(parameter)==vpiParameter,"parameter reference requires a declaration");canonical=canonical_parameter(parameter);}
 auto owner=parameter_owner(canonical);bool local=type(owner)==vpiFunction||type(owner)==vpiTask;
 if(local)need(inside_call&&current_function&&parameter_definition(owner,current_function),"parameter reference outside its lexical subroutine");
 const ParameterBinding* found=nullptr;
 for(auto& binding:parameter_bindings){
  if(canonical_key(binding.canonical)!=canonical_key(canonical))continue;
  if(std::none_of(parameter_visible_scopes.begin(),parameter_visible_scopes.end(),[&](H scope){return identity(scope)==identity(binding.context);}))continue;
  if(local&&identity(binding.scope)!=identity(current_function))continue;
  need(!found,"ambiguous visible parameter binding");found=&binding;
 }
 need(found,"parameter declaration is outside bound elaborated scopes");return found;
}
int parameter_reference_width(H reference){auto declaration=find_parameter_binding(reference)->declaration;return type(word_spec(declaration))==vpiIntTypespec?32:width(declaration);}
bool parameter_reference_signed(H reference){return declared_signed(find_parameter_binding(reference)->declaration);}
std::string parameter_literal(H value,int target){
 if(type(value)==vpiConstant)return bits(value);
 auto operands=many(value,vpiOperand);int operation=vpi_get(vpiOpType,value);
 need(type(value)==vpiOperation&&operands.size()==1&&(operation==vpiMinusOp||operation==vpiPlusOp)&&type(operands[0])==vpiConstant,"parameter must be materialized or a unary literal");
 auto b=bits(operands[0]);need(b.find_first_not_of("01")==std::string::npos,"captured parameter must have a known value");int w=std::max(target,int(b.size()));if(b.size()<size_t(w))b=std::string(w-b.size(),signed_expression(operands[0],0)&&b[0]=='1'?'1':'0')+b;
 if(operation==vpiMinusOp){bool carry=true;for(int i=int(b.size())-1;i>=0;i--){bool bit=b[i]=='0';b[i]=(bit!=carry)?'1':'0';carry=bit&&carry;}}
 return b;
}
std::string parameter_expr(H reference){
 auto found=find_parameter_binding(reference);auto declaration=found->declaration;auto spec=word_spec(declaration);need(type(spec)==vpiIntTypespec||type(spec)==vpiLogicTypespec||type(spec)==vpiBitTypespec,"unsupported captured parameter type");
 int w=type(spec)==vpiIntTypespec?32:width(declaration);std::string b;
 if(found->rhs)b=parameter_literal(found->rhs,w);
 else{auto object=static_cast<const UHDM::parameter*>(identity(declaration));need(!object->VpiValue().empty(),"unmaterialized parameter value");auto constant=store->MakeConstant();constant->VpiValue(object->VpiValue());constant->VpiSize(w);constant->VpiConstType(object->VpiConstType());b=constant_bits(constant);}
 need(!b.empty()&&b.find_first_not_of("01")==std::string::npos,"captured parameter must have a known value");
 if(b.size()>size_t(w))b=b.substr(b.size()-w);else if(b.size()<size_t(w))b=std::string(w-b.size(),found->rhs&&signed_expression(found->rhs,0)&&b[0]=='1'?'1':'0')+b;
 return "{\"kind\":\"const\",\"bits\":"+q(b)+(declared_signed(declaration)?",\"signed\":true":"")+"}";
}
// Constant control values are tracked separately from symbolic runtime expressions.
// Only known <=32-bit loop controls are eligible for structural unrolling.
struct KnownControl{uint64_t bits;int width;bool sign;};
std::map<std::string,KnownControl> control_values;
std::set<const void*> loop_controls;
uint64_t control_mask(int w){return (uint64_t(1)<<w)-1;}
uint64_t control_resize(KnownControl a,int width,bool sign){auto value=a.bits;if(sign&&a.sign&&(value&(uint64_t(1)<<(a.width-1))))value|=~control_mask(a.width);return value&control_mask(width);}
bool control_signed(H h,int depth=0){
 need(h&&depth<64,"control signedness depth exceeded");if(type(h)==vpiConstant)return signed_expression(h,0);if(is_parameter_reference(h))return parameter_reference_signed(h);auto key=local_key(h);if(!key.empty())return declared_signed(call_locals.at(key).declaration);if(type(h)!=vpiOperation)return false;int op=vpi_get(vpiOpType,h);if(op!=vpiAddOp&&op!=vpiSubOp&&op!=vpiMinusOp)return false;auto args=many(h,vpiOperand);return !args.empty()&&std::all_of(args.begin(),args.end(),[&](H a){return control_signed(a,depth+1);});
}
std::optional<KnownControl> control_value(H h,int depth=0,int context=0,std::optional<bool> forceSign={}){
 if(!h||depth>32||context>32)return {};if(auto original=original_expression(h))return control_value(original,depth+1,context,forceSign);
 if(type(h)==vpiConstant){auto b=bits(h);if(b.size()>32||b.find_first_not_of("01")!=std::string::npos)return {};return KnownControl{std::stoull(b,nullptr,2),int(b.size()),signed_expression(h,0)};}
 if(is_parameter_reference(h)){auto parameter=one(h,vpiActual);auto text=parameter_expr(h);auto pos=text.find("\"bits\":\"");if(pos==std::string::npos)return {};auto b=text.substr(pos+8);b=b.substr(0,b.find('"'));if(b.size()>32)return {};return KnownControl{std::stoull(b,nullptr,2),int(b.size()),parameter_reference_signed(h)};}
 auto key=local_key(h);if(!key.empty()){auto it=control_values.find(key);if(it!=control_values.end())return it->second;return {};}
 if(type(h)!=vpiOperation)return {};auto args=many(h,vpiOperand);int op=vpi_get(vpiOpType,h);if(args.size()==1&&op==vpiMinusOp){auto a=control_value(args[0],depth+1,context,forceSign);if(a){int w=std::max(a->width,context);bool sign=forceSign.value_or(control_signed(h));a->bits=(-control_resize(*a,w,sign))&control_mask(w);a->width=w;a->sign=sign;return a;}return {};}
 if(args.size()!=2)return {};bool comparison=op==vpiLtOp||op==vpiLeOp||op==vpiGtOp||op==vpiGeOp||op==vpiEqOp||op==vpiNeqOp;int operandWidth=std::max(width(args[0]),width(args[1]));if(!comparison)operandWidth=std::max(operandWidth,context);if(operandWidth>32)return {};bool sign=comparison?(control_signed(args[0])&&control_signed(args[1])):forceSign.value_or(control_signed(h));auto a=control_value(args[0],depth+1,operandWidth,sign),b=control_value(args[1],depth+1,operandWidth,sign);if(!a||!b)return {};
 int w=std::max(operandWidth,std::max(a->width,b->width));auto left=control_resize(*a,w,sign),right=control_resize(*b,w,sign);auto signed_value=[&](uint64_t value){return int64_t(sign&&(value&(uint64_t(1)<<(w-1)))?value|~control_mask(w):value);};
 switch(op){case vpiAddOp:return KnownControl{(left+right)&control_mask(w),w,sign};case vpiSubOp:return KnownControl{(left-right)&control_mask(w),w,sign};case vpiLtOp:return KnownControl{uint64_t(signed_value(left)<signed_value(right)),1,false};case vpiLeOp:return KnownControl{uint64_t(signed_value(left)<=signed_value(right)),1,false};case vpiGtOp:return KnownControl{uint64_t(signed_value(left)>signed_value(right)),1,false};case vpiGeOp:return KnownControl{uint64_t(signed_value(left)>=signed_value(right)),1,false};case vpiEqOp:return KnownControl{uint64_t(left==right),1,false};case vpiNeqOp:return KnownControl{uint64_t(left!=right),1,false};default:return {};}
}
H control_target(H h){if(type(h)==vpiAssignment)return one(h,vpiLhs);auto args=many(h,vpiOperand);need(type(h)==vpiOperation&&args.size()==1,"unsupported for increment");int op=vpi_get(vpiOpType,h);need(op==vpiPostIncOp||op==vpiPreIncOp||op==vpiPostDecOp||op==vpiPreDecOp,"unsupported for increment");return args[0];}
void assign_local(H h,int depth){
 bool control=loop_controls.count(identity(h));
 if(type(h)==vpiOperation){need(control,"increment only supported as for control");auto key=local_key(control_target(h));need(!key.empty()&&control_values.count(key),"for increment requires known counter");auto value=control_values.at(key);int op=vpi_get(vpiOpType,h);value.bits=(value.bits+((op==vpiPostDecOp||op==vpiPreDecOp)?uint64_t(-1):1))&control_mask(value.width);control_values[key]=value;auto& word=call_locals.at(key);std::string b;for(int bit=value.width-1;bit>=0;bit--)b+=(value.bits&(uint64_t(1)<<bit))?'1':'0';word.value=convert_word(word.declaration,"{\"kind\":\"const\",\"bits\":"+q(b)+(value.sign?",\"signed\":true":"")+"}");word.assigned=true;return;}
 // UHDM leaves VpiBlocking unset for the grammar's for-control assignments.
 // Only the exact initializer/increment AST identities receive that exemption.
 need((control||vpi_get(vpiBlocking,h))&&!one(h,vpiDelayControl)&&!one(h,vpiEventControl),"subroutine requires untimed blocking writes");auto target=one(h,vpiLhs);
 need(type(target)==vpiRefObj||type(target)==vpiReg||type(target)==vpiLogicVar||type(target)==vpiBitVar||type(target)==vpiIntVar||type(target)==vpiRefVar,"subroutine writes require whole local words");
 auto key=local_key(target);need(!key.empty(),"subroutine write is not bound to a local declaration: "+name(target));auto constant=control_value(one(h,vpiRhs),0,width(call_locals.at(key).declaration));auto value=expr(one(h,vpiRhs),depth+1);auto& word=call_locals.at(key);word.value=convert_word(word.declaration,value);word.assigned=true;
 control_values.erase(key);int w=width(word.declaration);if(constant&&w<=32){KnownControl known{control_resize(*constant,w,true),w,declared_signed(word.declaration)};control_values[key]=known;std::string b;for(int bit=w-1;bit>=0;bit--)b+=(known.bits&(uint64_t(1)<<bit))?'1':'0';word.value=convert_word(word.declaration,"{\"kind\":\"const\",\"bits\":"+q(b)+(known.sign?",\"signed\":true":"")+"}");}
 output_budget(word.value);
}
void add_local(H,std::string);
// Bound expressions may read only the counter and immutable elaborated constants.
// Body writes therefore cannot invalidate the static trip-count proof.
void counter_dependencies(H h,const std::string& counter,int depth=0){need(h&&depth<64,"for control expression depth exceeded");if(type(h)==vpiOperation){for(auto child:many(h,vpiOperand))counter_dependencies(child,counter,depth+1);return;}if(type(h)==vpiConstant||is_parameter_reference(h))return;need(local_key(h)==counter,"for bounds/increment may only read the counter and constants");}
struct LoopHandles{std::vector<H> items;~LoopHandles(){for(auto h:items)vpi_release_handle(h);}H add(const UHDM::BaseClass* object){auto h=::NewVpiHandle(object);items.push_back(h);return h;}};
void symbolic_sequence(std::vector<H> sequence,int depth);
void symbolic_stmt(H h,int depth){symbolic_sequence({h},depth);}
// Continuations are interpreted per branch, so a return skips all later statements,
// including enclosing loop increments. Merge only final path results, never a live
// local with the value from a path that already returned.
void symbolic_sequence(std::vector<H> sequence,int depth){
 need(depth<32,"subroutine statement depth exceeded");LoopHandles owned;
 while(!sequence.empty()){
  H h=sequence.front();sequence.erase(sequence.begin());need(h&&++nodes<=20000,"subroutine statement budget exceeded");
  if(type(h)==vpiBegin){auto items=many(h,vpiStmt);items.insert(items.end(),sequence.begin(),sequence.end());sequence=std::move(items);continue;}
  if(type(h)==vpiAssignment||(type(h)==vpiOperation&&loop_controls.count(identity(h)))){assign_local(h,depth);continue;}
  if(type(h)==vpiReturn){need(call_locals.count("$return"),"task return unsupported");auto value=one(h,vpiCondition);if(value){auto expression=expr(value,depth+1);auto& word=call_locals.at("$return");word.value=convert_word(word.declaration,expression);word.assigned=true;}need(call_locals.at("$return").assigned,"function return requires assigned result or expression");return;}
  if(type(h)==vpiRepeat){auto count=control_value(one(h,vpiCondition));need(count&&count->bits<=256&&!(count->sign&&(count->bits&(uint64_t(1)<<(count->width-1)))),"subroutine repeat requires known count0..256");std::vector<H> expanded(size_t(count->bits),one(h,vpiStmt));expanded.insert(expanded.end(),sequence.begin(),sequence.end());sequence=std::move(expanded);continue;}
  if(type(h)==vpiFor){
   auto object=static_cast<const UHDM::for_stmt*>(identity(h));
   for(auto local:many(h,vpiVariables)){need(!one(local,vpiExpr),"initialized loop declaration unsupported");add_local(local,"");}
   std::vector<H> init,inc;auto append=[&](std::vector<H>& list,const UHDM::BaseClass* object){if(object&&std::none_of(list.begin(),list.end(),[&](H old){return identity(old)==object;}))list.push_back(owned.add(object));};
   if(auto list=object->VpiForInitStmts())for(auto item:*list)append(init,item);append(init,object->VpiForInitStmt());if(auto list=object->VpiForIncStmts())for(auto item:*list)append(inc,item);append(inc,object->VpiForIncStmt());
   need(init.size()==1&&inc.size()==1&&type(init[0])==vpiAssignment&&(type(inc[0])==vpiAssignment||type(inc[0])==vpiOperation),"for requires one assignment initializer and increment");
   auto initial=one(init[0],vpiLhs);if(local_key(initial).empty()&&(type(initial)==vpiIntVar||type(initial)==vpiLogicVar||type(initial)==vpiBitVar))add_local(initial,"$loop"+std::to_string(reinterpret_cast<uintptr_t>(identity(initial))));
   auto key=local_key(initial);need(!key.empty()&&key==local_key(control_target(inc[0]))&&width(call_locals.at(key).declaration)<=32,"for requires one <=32-bit local counter");
   // Counter mutation by the body would invalidate the proven trip count.
   std::vector<H> visit{one(h,vpiStmt)};std::set<const void*> body_seen;
   while(!visit.empty()){
    auto item=visit.back();visit.pop_back();if(!item||!body_seen.insert(identity(item)).second)continue;need(body_seen.size()<=20000,"for body traversal budget exceeded");
    if(type(item)==vpiAssignment)need(local_key(one(item,vpiLhs))!=key,"for body cannot mutate its counter");
    if(type(item)==vpiOperation){int op=vpi_get(vpiOpType,item);if(op==vpiPostIncOp||op==vpiPreIncOp||op==vpiPostDecOp||op==vpiPreDecOp)need(local_key(control_target(item))!=key,"for body cannot mutate its counter");}
    // Nested loop controls execute in the outer body and can mutate its counter.
    // Traverse both UHDM representations, with identity deduplication and a bound.
    if(type(item)==vpiFor){auto nested=static_cast<const UHDM::for_stmt*>(identity(item));if(auto list=nested->VpiForInitStmts())for(auto part:*list)visit.push_back(owned.add(part));if(auto part=nested->VpiForInitStmt())visit.push_back(owned.add(part));if(auto list=nested->VpiForIncStmts())for(auto part:*list)visit.push_back(owned.add(part));if(auto part=nested->VpiForIncStmt())visit.push_back(owned.add(part));}
    for(auto child:many(item,vpiStmt))visit.push_back(child);if(auto child=one(item,vpiStmt))visit.push_back(child);if(auto child=one(item,vpiElseStmt))visit.push_back(child);
   }
   counter_dependencies(one(h,vpiCondition),key);counter_dependencies(type(inc[0])==vpiAssignment?one(inc[0],vpiRhs):control_target(inc[0]),key);loop_controls.insert(identity(init[0]));loop_controls.insert(identity(inc[0]));
   auto saved=call_locals;auto controls=control_values;assign_local(init[0],depth+1);std::vector<H> expanded{init[0]};std::set<uint64_t> seen;
   for(unsigned i=0;;i++){auto condition=control_value(one(h,vpiCondition));need(condition.has_value(),"for condition must be known and independent of runtime inputs");if(!condition->bits)break;need(i<256&&control_values.count(key)&&seen.insert(control_values.at(key).bits).second,"for iteration budget or counter progress exceeded");expanded.push_back(one(h,vpiStmt));expanded.push_back(inc[0]);assign_local(inc[0],depth+1);}
   call_locals=saved;control_values=controls;expanded.insert(expanded.end(),sequence.begin(),sequence.end());sequence=std::move(expanded);continue;
  }
  if(type(h)==vpiIf||type(h)==vpiIfElse){
   auto condition=expr(one(h,vpiCondition),depth+1);auto before=call_locals;auto controls=control_values;
   auto yesSequence=sequence;yesSequence.insert(yesSequence.begin(),one(h,vpiStmt));symbolic_sequence(std::move(yesSequence),depth+1);auto yes=call_locals;
   call_locals=before;control_values=controls;auto noSequence=sequence;if(auto other=one(h,vpiElseStmt))noSequence.insert(noSequence.begin(),other);symbolic_sequence(std::move(noSequence),depth+1);auto no=call_locals;
   auto truth="{\"kind\":\"caseeq\",\"args\":[{\"kind\":\"redor\",\"args\":["+condition+"]},{\"kind\":\"const\",\"bits\":\"1\"}]}";
   control_values.clear();call_locals=before; // Branch-only loop locals do not escape their lexical path.
   for(auto& [key,word]:call_locals){need(yes.at(key).value.size()+no.at(key).value.size()+truth.size()<1024*1024,"symbolic branch expansion exceeds1MiB");word.assigned=yes.at(key).assigned&&no.at(key).assigned;if(word.assigned){word.value="{\"kind\":\"cond\",\"args\":["+truth+","+yes.at(key).value+","+no.at(key).value+"]}";output_budget(word.value);}}
   return;
  }
  throw std::runtime_error("unsupported pure subroutine statement type "+std::to_string(type(h)));
 }
}
struct CallFrame{
 std::map<std::string,LocalWord> saved;std::map<std::string,KnownControl> controls;bool was_inside;const void* id;H previous_function;
 CallFrame(H function):saved(call_locals),controls(control_values),was_inside(inside_call),id(identity(function)),previous_function(current_function){need(active_calls.size()<16&&active_calls.insert(id).second,"recursive or excessive subroutine calls");call_locals.clear();control_values.clear();inside_call=true;current_function=function;}
 ~CallFrame(){call_locals=std::move(saved);control_values=std::move(controls);inside_call=was_inside;current_function=previous_function;active_calls.erase(id);}
};
void add_local(H h,std::string key=""){if(key.empty())key=name(h);need(h&&!key.empty()&&!one(h,vpiExpr),"initialized or unnamed subroutine local unsupported");if(type(word_spec(h))!=vpiIntTypespec)logic_word(h);need(call_locals.size()<64,"subroutine local budget exceeded");auto [it,added]=call_locals.emplace(key,LocalWord{h,"",false});need(added||identity(it->second.declaration)==identity(h),"duplicate subroutine local name");}
std::vector<H> prepare_call(H function,const std::vector<std::string>& values,bool task){
 need(vpi_get(vpiAutomatic,function),"only automatic subroutines supported");bind_parameters(function);
 auto formals=many(function,vpiIODecl);need(formals.size()==values.size()&&formals.size()<=32,"subroutine arity mismatch or budget exceeded");
 for(int relation:{vpiReg,vpiVariables})for(auto local:many(function,relation))add_local(local,"");
 for(size_t i=0;i<formals.size();i++){
  int direction=vpi_get(vpiDirection,formals[i]);need(direction==vpiInput||(task&&direction==vpiOutput),"subroutine supports input formals and task outputs only");
  auto declaration=formal_word(formals[i]);auto key=name(declaration);if(!call_locals.count(key))add_local(declaration);
  auto& word=call_locals.at(key);if(direction==vpiInput){word.value=convert_word(word.declaration,values[i]);word.assigned=true;}
 }
 return formals;
}
std::string lower_function(H call,int depth){
 auto function=callee(call,true);std::vector<std::string> values;for(auto actual:many(call,vpiArgument))values.push_back(expr(actual,depth+1));
 CallFrame frame(function);prepare_call(function,values,false);auto result=one(function,vpiReturn);need(result,"function return declaration required");add_local(result,"$return");
 symbolic_stmt(one(function,vpiStmt),depth+1);auto word=call_locals.at("$return");need(word.assigned,"function return must be assigned on every path");output_budget(word.value);return word.value;
}
std::string lower_task(H call,int depth){
 need(!inside_call,"nested task calls unsupported");auto function=callee(call,false);auto actuals=many(call,vpiArgument),formals=many(function,vpiIODecl);need(actuals.size()==formals.size(),"task arity mismatch");
 std::vector<std::string> values,targets;std::set<std::string> outputs;
 for(size_t i=0;i<actuals.size();i++){
  if(vpi_get(vpiDirection,formals[i])==vpiOutput){need(type(actuals[i])==vpiRefObj||type(actuals[i])==vpiReg||type(actuals[i])==vpiLogicVar,"task outputs require whole words");auto target=signal_name(actuals[i]);need(outputs.insert(target).second,"aliased task outputs unsupported");targets.push_back(expr(actuals[i],depth+1));values.push_back("");}
  else{values.push_back(expr(actuals[i],depth+1));targets.push_back("");}
 }
 CallFrame frame(function);prepare_call(function,values,true);symbolic_stmt(one(function,vpiStmt),depth+1);
 std::vector<std::string> destinations,results;int total=0;
 for(size_t i=0;i<formals.size();i++)if(vpi_get(vpiDirection,formals[i])==vpiOutput){
  auto word=call_locals.at(name(formal_word(formals[i])));need(word.assigned,"task outputs require assignment on every path");
  destinations.push_back(targets[i]);results.push_back(convert_word(actuals[i],word.value));total+=width(actuals[i]);need(total<=128,"task combined outputs exceed128bits");
 }
 if(destinations.empty())return "{\"kind\":\"block\",\"items\":[]}";
 // One concatenated write snapshots every result before any aliased input can be changed by copy-out.
 return "{\"kind\":\"blocking\",\"target\":"+concat_tree(destinations,0,destinations.size())+",\"value\":"+concat_tree(results,0,results.size())+"}";
}
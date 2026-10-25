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
 for(int i=0;h&&i<64;i++){for(auto& [key,word]:call_locals)if(same_local(h,word.declaration))return key;auto actual=one(h,vpiActual);if(identity(actual)==identity(h))break;h=actual;}
 return "";
}
std::string convert_word(H declaration,const std::string& value){output_budget(value);for(int i=0;i<64;i++){auto actual=one(declaration,vpiActual);if(!actual||identity(actual)==identity(declaration))break;declaration=actual;}logic_word(declaration);auto spec=word_spec(declaration);return "{\"kind\":\"convert\",\"value\":"+value+",\"width\":"+std::to_string(width(declaration))+",\"signed\":"+(declared_signed(declaration)?"true":"false")+",\"two_state\":"+((type(declaration)==vpiBitVar||type(spec)==vpiBitTypespec)?"true":"false")+"}";}
void symbolic_stmt(H h,int depth){
 need(h&&depth<32&&++nodes<=20000,"subroutine statement budget exceeded");
 if(type(h)==vpiBegin){for(auto child:many(h,vpiStmt))symbolic_stmt(child,depth+1);return;}
 if(type(h)==vpiAssignment){need(vpi_get(vpiBlocking,h)&&!one(h,vpiDelayControl)&&!one(h,vpiEventControl),"subroutine requires untimed blocking writes");auto target=one(h,vpiLhs);need(type(target)==vpiRefObj||type(target)==vpiReg||type(target)==vpiLogicVar||type(target)==vpiBitVar,"subroutine writes require whole local words");auto key=local_key(target);need(!key.empty(),"subroutine write is not bound to a local declaration: "+name(target));auto value=expr(one(h,vpiRhs),depth+1);auto& word=call_locals.at(key);word.value=convert_word(word.declaration,value);word.assigned=true;output_budget(word.value);return;}
 if(type(h)==vpiIf||type(h)==vpiIfElse){
  auto condition=expr(one(h,vpiCondition),depth+1);auto before=call_locals;symbolic_stmt(one(h,vpiStmt),depth+1);auto yes=call_locals;call_locals=before;if(auto other=one(h,vpiElseStmt))symbolic_stmt(other,depth+1);auto no=call_locals;
  // Procedural if takes its then arm only for a known true value, unlike ?: X merging.
  auto truth="{\"kind\":\"caseeq\",\"args\":[{\"kind\":\"redor\",\"args\":["+condition+"]},{\"kind\":\"const\",\"bits\":\"1\"}]}";
  for(auto& [key,word]:call_locals){need(yes.at(key).value.size()+no.at(key).value.size()+truth.size()<1024*1024,"symbolic branch expansion exceeds1MiB");word.assigned=yes.at(key).assigned&&no.at(key).assigned;if(word.assigned)word.value="{\"kind\":\"cond\",\"args\":["+truth+","+yes.at(key).value+","+no.at(key).value+"]}";}return;
 }
 throw std::runtime_error("unsupported pure subroutine statement type "+std::to_string(type(h)));
}
struct CallFrame{
 std::map<std::string,LocalWord> saved;bool was_inside;const void* id;
 CallFrame(H function):saved(call_locals),was_inside(inside_call),id(identity(function)){need(active_calls.size()<16&&active_calls.insert(id).second,"recursive or excessive subroutine calls");call_locals.clear();inside_call=true;}
 ~CallFrame(){call_locals=std::move(saved);inside_call=was_inside;active_calls.erase(id);}
};
void add_local(H h,std::string key=""){if(key.empty())key=name(h);need(h&&!key.empty()&&!one(h,vpiExpr),"initialized or unnamed subroutine local unsupported");logic_word(h);need(call_locals.size()<64,"subroutine local budget exceeded");auto [it,added]=call_locals.emplace(key,LocalWord{h,"",false});need(added||identity(it->second.declaration)==identity(h),"duplicate subroutine local name");}
std::vector<H> prepare_call(H function,const std::vector<std::string>& values,bool task){
 need(vpi_get(vpiAutomatic,function),"only automatic subroutines supported");
 auto formals=many(function,vpiIODecl);need(formals.size()==values.size()&&formals.size()<=32,"subroutine arity mismatch or budget exceeded");
 for(int relation:{vpiReg,vpiVariables})for(auto local:many(function,relation))add_local(local);
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
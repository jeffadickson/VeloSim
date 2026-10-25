// Elaboration may fold literal expressions using UHDM's 64-bit arithmetic.
// Recover their original syntax by exact source span, then lower with RTL sizing.
std::map<std::string,H> original_expressions;
std::set<const void*> original_visited;
std::string expression_location(H h){
 auto object=static_cast<const UHDM::any*>(identity(h));if(!object||!object->VpiLineNo()||!object->VpiColumnNo())return "";
 auto parent=object;std::string file;for(int depth=0;parent&&depth<128;++depth){file=parent->VpiFile();if(!file.empty())break;auto next=parent->VpiParent();if(next==parent)break;parent=next;}if(file.empty())return "";
 return file+":"+std::to_string(object->VpiLineNo())+":"+std::to_string(object->VpiColumnNo())+":"+std::to_string(object->VpiEndLineNo())+":"+std::to_string(object->VpiEndColumnNo());
}
void collect_original_expressions(H h,int depth=0){
 if(!h||!original_visited.insert(identity(h)).second)return;
 need(depth<128&&original_visited.size()<=100000,"original syntax traversal budget exceeded");
 if(type(h)==vpiOperation||type(h)==vpiRefObj||type(h)==vpiSysFuncCall||type(h)==vpiConstant){auto key=expression_location(h);if(!key.empty()){auto [entry,inserted]=original_expressions.emplace(key,h);if(!inserted&&identity(entry->second)!=identity(h)){bool same=false;if(type(h)==vpiConstant&&type(entry->second)==vpiConstant){auto a=static_cast<const UHDM::constant*>(identity(h)),b=static_cast<const UHDM::constant*>(identity(entry->second));same=a->VpiSize()==b->VpiSize()&&a->VpiValue()==b->VpiValue()&&a->VpiConstType()==b->VpiConstType()&&a->VpiDecompile()==b->VpiDecompile()&&vpi_get(vpiSigned,h)==vpi_get(vpiSigned,entry->second);}if(!same)entry->second=nullptr;}}}
 for(int relation:{vpiStmt,vpiCondition,vpiElseStmt,vpiLhs,vpiRhs,vpiIndex,vpiBaseExpr,vpiWidthExpr,vpiLeftRange,vpiRightRange,vpiHighConn,vpiLowConn,vpiExpr})collect_original_expressions(one(h,relation),depth+1);
 for(int relation:{vpiModule,vpiTaskFunc,vpiPort,vpiVariables,vpiReg,vpiNet,vpiProcess,vpiContAssign,vpiParamAssign,vpiStmt,vpiOperand,vpiArgument,vpiExpr,vpiCaseItem,vpiGenScopeArray,vpiGenScope,vpiGenStmt})for(auto child:many(h,relation))collect_original_expressions(child,depth+1);
}
H original_expression(H h){
 if(type(h)!=vpiConstant)return nullptr;auto key=expression_location(h);auto found=original_expressions.find(key);
 if(found==original_expressions.end())return nullptr;need(found->second,"ambiguous original expression source span");return identity(found->second)==identity(h)?nullptr:found->second;
}

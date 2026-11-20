/*
 Copyright 2019 Alain Dargelas

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0

 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.
 */
// Adapted from pinned Surelog CompileHelper.cpp; preserve omitted slots and validate names.
VectorOfany* CompileHelper::compileTfCallArguments(
    DesignComponent* component, const FileContent* fC, NodeId Arg_list_node,
    CompileDesign* compileDesign, Reduce reduce, UHDM::any* call,
    ValuedComponentI* instance, bool muteErrors) {
  FileSystem* const fileSystem = FileSystem::getInstance();
  UHDM::Serializer& s = compileDesign->getSerializer();
  if (fC->Type(Arg_list_node) == VObjectType::paSelect) {
    // Task or func call with no argument, not even ()
    return nullptr;
  }
  NodeId argumentNode = fC->Child(Arg_list_node);
  if (!argumentNode) return nullptr;
  VectorOfio_decl* io_decls = nullptr;
  if (const func_call* tf = any_cast<func_call*>(call)) {
    const function* func = tf->Function();
    if (func) io_decls = func->Io_decls();
  } else if (const task_call* tf = any_cast<task_call*>(call)) {
    const task* task = tf->Task();
    if (task) io_decls = task->Io_decls();
  }
  VectorOfany* arguments = s.MakeAnyVec();
  std::map<std::string, any*, std::less<>> args;
  bool seenNamed = false;
  bool invalidNames = false;
  // Preserve an omission as a typed sentinel, never a numeric argument zero.
  // This decompilation string cannot spell an integral source literal.
  const auto marker = [&](bool invalid, NodeId location) {
    constant* c = s.MakeConstant();
    c->VpiValue("INT:0");
    c->VpiDecompile(invalid ? "$velosim_invalid_actual_v4" : "$velosim_omitted_actual_v4");
    c->VpiSize(64);
    c->VpiConstType(vpiIntConst);
    c->VpiParent(call);
    if (location) fC->populateCoreMembers(location, location, c);
    return c;
  };
  // The pinned grammar starts with expression? followed by (COMMA argument)*.
  // A first Argument node therefore belongs to the comma tail: the optional
  // first expression is absent. Restore exactly that leading slot from the
  // parsed node kind, independent of source spacing, comments or line breaks.
  if (fC->Type(Arg_list_node) == VObjectType::paList_of_arguments &&
      fC->Type(argumentNode) == VObjectType::paArgument) {
    arguments->push_back(marker(false, Arg_list_node));
  }
  while (argumentNode) {
    NodeId argument = argumentNode;
    if (fC->Type(argument) == VObjectType::paArgument) {
      argument = fC->Child(argument);
    }
    NodeId sibling = fC->Sibling(argument);
    NodeId Expression;
    if (fC->Type(argument) == VObjectType::slStringConst) {
      seenNamed = true;
      const bool hasExpression = fC->Type(sibling) == VObjectType::paExpression;
      UHDM::any* exp = hasExpression
          ? compileExpression(component, fC, sibling, compileDesign, reduce, call, instance, muteErrors)
          : marker(false, argument);
      if (!exp) exp = marker(true, argument);
      if (exp->VpiParent() == nullptr) exp->VpiParent(call);
      if (!args.emplace(fC->SymName(argument), exp).second) invalidNames = true;
      if (hasExpression) argumentNode = fC->Sibling(argumentNode);
    } else if (((fC->Type(argument) == VObjectType::paUnary_Tilda) ||
                (fC->Type(argument) == VObjectType::paUnary_Not)) &&
               (fC->Type(sibling) == VObjectType::paExpression)) {
      // arg by position
      if (seenNamed) invalidNames = true;
      Expression = argument;
      UHDM::any* exp =
          compileExpression(component, fC, Expression, compileDesign, reduce,
                            call, instance, muteErrors);
      if (exp) {
        if (exp->VpiParent() == nullptr) exp->VpiParent(call);
        arguments->push_back(exp);
      } else {
        arguments->push_back(marker(true, argument));
      }
      argumentNode = fC->Sibling(argumentNode);
    } else {
      // arg by position
      if (seenNamed) invalidNames = true;
      Expression = argument;
      if (Expression) {
        UHDM::any* exp =
            compileExpression(component, fC, Expression, compileDesign, reduce,
                              call, instance, muteErrors);
        if (exp) {
          arguments->push_back(exp);
          if (exp->VpiParent() == nullptr) exp->VpiParent(call);
        } else {
          arguments->push_back(marker(true, argument));
        }
      } else {
        arguments->push_back(marker(false, argumentNode));
      }
    }
    argumentNode = fC->Sibling(argumentNode);
  }
  if (NodeId clocking = fC->Sibling(Arg_list_node)) {
    if (fC->Type(clocking) == VObjectType::paClocking_event) {
      UHDM::any* exp = compileExpression(component, fC, clocking, compileDesign,
                                         reduce, call, instance, muteErrors);
      if (exp) {
        arguments->push_back(exp);
        if (exp->VpiParent() == nullptr) exp->VpiParent(call);
      }
    }
  }
  if (!args.empty()) {
    if (io_decls) {
      const size_t leading = arguments->size();
      if (leading > io_decls->size()) invalidNames = true;
      for (const auto& named : args) {
        const auto& key = named.first;
        bool found = false;
        for (size_t i = leading; i < io_decls->size(); ++i)
          if ((*io_decls)[i]->VpiName() == key) { found = true; break; }
        if (!found) invalidNames = true;  // unknown name or repeated positional formal
      }
      for (size_t i = leading; i < io_decls->size(); ++i) {
        const auto found = args.find((*io_decls)[i]->VpiName());
        arguments->push_back(found != args.end() ? found->second : marker(false, NodeId{}));
      }
    } else {
      // A named call cannot be normalized without its typed signature.
      invalidNames = true;
    }
  }
  if (invalidNames) arguments->push_back(marker(true, Arg_list_node));
  return arguments;
}


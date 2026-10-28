// Normalize a private evaluator argument vector. Serialized call slots remain
// intact for the runtime importer. Never let an omission sentinel's payload zero
// reach UHDM's constant evaluator (in particular, packed-width evaluation).
  std::vector<any*> effectiveArguments;
  const auto argumentFailure = [&](const char* reason) -> expr* {
    auto* symbols = compileDesign->getCompiler()->getSymbolTable();
    Location location(fileId, lineNumber, pexpr ? pexpr->VpiColumnNo() : 0,
                      symbols->registerSymbol(reason));
    Error error(ErrorDefinition::UHDM_UNSUPPORTED_EXPR, location);
    compileDesign->getCompiler()->getErrorContainer()->addError(error);
    invalidValue = true;
    return nullptr;
  };
  const auto* formals = func ? func->Io_decls() : nullptr;
  if (args) effectiveArguments = *args;
  if (formals && effectiveArguments.size() > formals->size())
    return argumentFailure("too many constant-function arguments");
  // Constant defaults currently accept integral constant/parameter expressions.
  // Broader runtime defaults are lowered separately. Reject unsupported shapes
  // here rather than silently folding a mutable reference or function to zero.
  size_t defaultExpressionBudget = 20000;
  std::function<bool(any*, unsigned)> constantDefault =
      [&](any* value, unsigned depth) -> bool {
    if (!value || depth >= 32 || defaultExpressionBudget == 0) return false;
    --defaultExpressionBudget;
    if (any_cast<constant*>(value)) return true;
    if (auto* operation = any_cast<UHDM::operation*>(value)) {
      const auto* operands = operation->Operands();
      if (!operands || operands->size() > 32) return false;
      for (auto* operand : *operands)
        if (!constantDefault(operand, depth + 1)) return false;
      return true;
    }
    if (auto* reference = any_cast<ref_obj*>(value)) {
      any* actual = reference->Actual_group();
      if (!actual) actual = getObject(reference->VpiName(), component,
                                      compileDesign, instance, value);
      return any_cast<parameter*>(actual) || any_cast<enum_const*>(actual) ||
             any_cast<constant*>(actual);
    }
    return false;
  };
  if (formals) {
    for (size_t i = 0; i < formals->size(); ++i) {
      any* value = i < effectiveArguments.size() ? effectiveArguments[i] : nullptr;
      if (auto* literal = any_cast<constant*>(value)) {
        if (literal->VpiDecompile() == "$velosim_invalid_actual_v4")
          return argumentFailure("invalid named or positional constant-function arguments");
        if (literal->VpiDecompile() == "$velosim_omitted_actual_v4") value = nullptr;
      }
      if (value) continue;
      auto* formal = (*formals)[i];
      if (!formal || formal->VpiDirection() != vpiInput)
        return argumentFailure("omitted constant-function argument requires an input default");
      any* initial = formal->Expr();
      if (auto* storage = any_cast<variables*>(initial)) initial = storage->Expr();
      if (!initial)
        return argumentFailure("missing constant-function argument requires an explicit default");
      if (!constantDefault(initial, 0))
        return argumentFailure("constant-function argument default requires a supported constant expression");
      bool invalidDefault = false;
      expr* reduced = reduceExpr(initial, invalidDefault, component, compileDesign,
                                instance, fileId, lineNumber, initial);
      if (invalidDefault || !any_cast<constant*>(reduced))
        return argumentFailure("constant-function argument default could not be evaluated");
      if (i < effectiveArguments.size()) effectiveArguments[i] = reduced;
      else effectiveArguments.push_back(reduced);
    }
  }
  args = &effectiveArguments;

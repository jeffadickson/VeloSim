    // Preserve parsed time metadata that this pinned writer otherwise omits.
    // Only emit provenance when both scales are explicit and representable.
    // Unknown/default frontend values must not silently become seconds.
    if (mod && mod->getDesignElement()) {
      const auto& time = mod->getDesignElement()->m_timeInfo;
      const auto valid = [](double value) { return value == 1 || value == 10 || value == 100; };
      if (time.m_type != TimeInfo::Type::None && valid(time.m_timeUnitValue) && valid(time.m_timePrecisionValue)) {
        const auto exponent = [](TimeInfo::Unit unit, double value) {
          return -3 * static_cast<int>(unit) + (value == 100 ? 2 : value == 10 ? 1 : 0);
        };
        auto module = static_cast<module_inst*>(m);
        module->VpiTimeUnit(exponent(time.m_timeUnit,time.m_timeUnitValue));
        module->VpiTimePrecision(exponent(time.m_timePrecision,time.m_timePrecisionValue));
        auto marker = s.MakeAttribute();
        marker->VpiName("velosim frontend time scale v1");
        marker->VpiParent(module);
        if (!module->Attributes()) module->Attributes(s.MakeAttributeVec());
        module->Attributes()->push_back(marker);
        auto triggerMarker = s.MakeAttribute();
        triggerMarker->VpiName("velosim frontend named triggers v1");
        triggerMarker->VpiParent(module);
        module->Attributes()->push_back(triggerMarker);
      }
    }
    // Argument-slot provenance does not depend on explicit time declarations.
    const auto argumentErrors = m_compileDesign->getCompiler()->getErrorContainer()->getErrorStats();
    if (m && argumentErrors.nbFatal == 0 && argumentErrors.nbSyntax == 0 && argumentErrors.nbError == 0) {
      auto argumentModule = static_cast<module_inst*>(m);
      auto argumentMarker = s.MakeAttribute();
      argumentMarker->VpiName("velosim frontend argument slots v4");
      argumentMarker->VpiParent(argumentModule);
      if (!argumentModule->Attributes()) argumentModule->Attributes(s.MakeAttributeVec());
      argumentModule->Attributes()->push_back(argumentMarker);
    }

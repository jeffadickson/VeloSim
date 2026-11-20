# Schematic solver bridge plan

1. Translate the existing terminal graph into a deterministic ground-first
   circuit. Test named connections, explicit junctions, unsupported devices,
   controlled-source orientation, and stale commands in Core.
2. Map typed DC operating point, explicit AC frequency grid and transient
   settings onto the public native JSON contract. Reuse the existing process
   boundary. Add result validation and a browser HTTP adapter with typed JSON.
3. Exercise one mixed RLC/source schematic through a browser and stdio MCP.
   Compare the same named output against an independently authored ngspice
   deck and report error and analysis limits.
4. Extend only through native model contracts with reference evidence. Keep
   schematic placement, accepted parameters and numerical qualification
   separate. The installed native solver is reused, ngspice serves as an
   external oracle, and no new production dependency is needed.

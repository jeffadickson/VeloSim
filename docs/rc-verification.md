# Native RC charging verification

The lesson uses the repository's Rust simulator executable built from Spice revision 3300b25ac481e885f142c53dc81cb284a99bb172. Its SHA-256 is cf642c3f234720742794ebadb8dedc4a6fa6b91640f133bf53edaad261699cb5.

For 1 kΩ, 1 µF and a 5 V source, the native trapezoidal solve recorded 502 samples for probe P1. The finite rise is 10 µs and the stop time is 5.01 ms. The MCP smoke test compared every returned sample with the analytical finite-ramp response and measured a maximum absolute error of 41.3 µV. It retrieved the same run by ID and rejected an unknown probe. The unit test removes the capacitor and verifies the unsupported circuit does not call the solver. An isolated mutation that redirected the output node to ground was caught.

The first diagnostic run used a fixed 1 µs rise and a 10 µs maximum step. The solver stepped over the end of that rise, producing about 22 mV of early error. The qualified lesson uses an explicitly constrained pulse whose rise coincides with the first step. This evidence covers the bounded lesson. Arbitrary pulse timings and general edge handling require separate verification.

Thirteen tests passed, including an edit during an in-flight solve that preserved the run revision. Coverlet measured 279/362 lines and 135/248 branches in the C# core. Browser, MCP transport, native bridge and Rust coverage are outside that collector's scope. StrictDoc 0.30.4 parsed nine documents and 236 unique requirement IDs.

The published Chromium iframe displayed the 502-sample P1 trace. The test checked the plotted voltage near one time constant and the 4.9665 V final readout. The loopback host serves fingerprinted ICU data with an explicit MIME mapping. The final screenshot and raw logs are under E:/Artifacts/SchematicLab-rc-cb2bbcf2.

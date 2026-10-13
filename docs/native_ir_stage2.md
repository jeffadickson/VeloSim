# Native multibit process kernel — stage 2

The next VeloSim prototype implements a C++ kernel for 1–64-bit four-state signals, parallel evaluation of edge-triggered processes, and simultaneous nonblocking-style commits. Seven new tests passed locally, including 1/2/4/8/16-thread equivalence, counter rollover, synchronous reset, and X/Z copy behavior.

**Important:** The tested code is currently distributed as a separate source ZIP in the development conversation; it has not yet been imported into the GitHub repository. The current UHDM reader also remains uncompiled against the real library.

This kernel supports only programmatically registered COPY, INC and RESET_INC processes. It is not a complete IEEE event scheduler, and conservative X propagation for arithmetic is not fully standards-equivalent.

Next: import the tested native source, compile the pinned UHDM reader, lower UHDM objects into a versioned IR, and run differential RTL tests.

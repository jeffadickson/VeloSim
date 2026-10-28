# SIMD timing results

Measured on 2026-10-26. See [raw samples, normalized medians and source hashes](../verification/svint-simd-timings.json).

Three independent runs on an Intel Core i5-10505 under WSL2 Ubuntu 24.04, pinned to logical CPU 0. Clang 18.1.3 Release; coverage and sanitizers off. Each of 54 cases uses two warmups and seven measured batches per run. Backend order rotates. Values below are medians of 21 normalized batches. Parsing is excluded; result allocation, adapter copies, destruction and full-word checksum are included.

All three runs passed: each had 6,912 adapter truth-table comparisons and 1,728 comparisons with the existing VeloSim runtime. Timed checksums matched unchanged SVInt. Repeated checks use the same seed and are not additional distinct test cases.

| Bits | Inputs | Op | SVInt ns/op | AVX128 ns/op | SVInt / AVX128 | Scalar adapter / AVX128 |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| 127 | known | and | 28.05 | 42.83 | 0.65 | 1.03 |
| 127 | known | or | 26.29 | 43.70 | 0.60 | 1.02 |
| 127 | known | xor | 26.29 | 43.14 | 0.61 | 1.03 |
| 127 | mixed_left | and | 32.09 | 43.80 | 0.73 | 1.08 |
| 127 | mixed_left | or | 33.04 | 43.92 | 0.75 | 1.05 |
| 127 | mixed_left | xor | 26.25 | 42.75 | 0.61 | 1.02 |
| 127 | mixed_both | and | 32.30 | 43.97 | 0.73 | 1.06 |
| 127 | mixed_both | or | 32.88 | 43.73 | 0.75 | 1.04 |
| 127 | mixed_both | xor | 27.97 | 42.87 | 0.65 | 1.02 |
| 128 | known | and | 26.70 | 42.14 | 0.63 | 1.02 |
| 128 | known | or | 25.21 | 42.39 | 0.59 | 1.01 |
| 128 | known | xor | 24.27 | 41.31 | 0.59 | 1.03 |
| 128 | mixed_left | and | 30.15 | 42.12 | 0.72 | 1.06 |
| 128 | mixed_left | or | 29.89 | 42.91 | 0.70 | 1.05 |
| 128 | mixed_left | xor | 25.80 | 42.54 | 0.61 | 1.01 |
| 128 | mixed_both | and | 30.51 | 42.98 | 0.71 | 1.05 |
| 128 | mixed_both | or | 31.90 | 43.14 | 0.74 | 1.04 |
| 128 | mixed_both | xor | 26.41 | 42.26 | 0.62 | 1.01 |
| 256 | known | and | 26.39 | 46.06 | 0.57 | 0.98 |
| 256 | known | or | 26.54 | 47.12 | 0.56 | 0.98 |
| 256 | known | xor | 25.86 | 43.57 | 0.59 | 1.06 |
| 256 | mixed_left | and | 33.63 | 43.70 | 0.77 | 1.11 |
| 256 | mixed_left | or | 33.55 | 43.59 | 0.77 | 1.14 |
| 256 | mixed_left | xor | 27.98 | 43.81 | 0.64 | 1.05 |
| 256 | mixed_both | and | 35.29 | 46.46 | 0.76 | 1.08 |
| 256 | mixed_both | or | 35.72 | 45.93 | 0.78 | 1.10 |
| 256 | mixed_both | xor | 31.00 | 44.84 | 0.69 | 1.05 |
| 1024 | known | and | 42.00 | 63.82 | 0.66 | 0.92 |
| 1024 | known | or | 42.10 | 63.07 | 0.67 | 0.92 |
| 1024 | known | xor | 40.92 | 56.49 | 0.72 | 1.27 |
| 1024 | mixed_left | and | 73.67 | 74.30 | 0.99 | 1.28 |
| 1024 | mixed_left | or | 73.56 | 76.12 | 0.97 | 1.28 |
| 1024 | mixed_left | xor | 61.13 | 71.61 | 0.85 | 1.22 |
| 1024 | mixed_both | and | 79.87 | 77.20 | 1.03 | 1.25 |
| 1024 | mixed_both | or | 80.12 | 76.47 | 1.05 | 1.27 |
| 1024 | mixed_both | xor | 70.38 | 72.84 | 0.97 | 1.15 |
| 4096 | known | and | 122.56 | 160.80 | 0.76 | 0.87 |
| 4096 | known | or | 123.28 | 159.39 | 0.77 | 0.87 |
| 4096 | known | xor | 126.27 | 139.72 | 0.90 | 1.45 |
| 4096 | mixed_left | and | 246.76 | 220.24 | 1.12 | 1.36 |
| 4096 | mixed_left | or | 255.15 | 213.18 | 1.20 | 1.41 |
| 4096 | mixed_left | xor | 202.66 | 201.76 | 1.00 | 1.19 |
| 4096 | mixed_both | and | 270.82 | 234.16 | 1.16 | 1.28 |
| 4096 | mixed_both | or | 270.51 | 222.36 | 1.22 | 1.34 |
| 4096 | mixed_both | xor | 244.48 | 211.59 | 1.16 | 1.12 |
| 16384 | known | and | 517.77 | 662.42 | 0.78 | 0.88 |
| 16384 | known | or | 518.69 | 652.80 | 0.79 | 0.92 |
| 16384 | known | xor | 523.33 | 565.61 | 0.93 | 1.39 |
| 16384 | mixed_left | and | 998.40 | 894.28 | 1.12 | 1.39 |
| 16384 | mixed_left | or | 979.26 | 850.56 | 1.15 | 1.41 |
| 16384 | mixed_left | xor | 799.24 | 805.30 | 0.99 | 1.19 |
| 16384 | mixed_both | and | 1088.31 | 922.39 | 1.18 | 1.31 |
| 16384 | mixed_both | or | 1067.31 | 896.27 | 1.19 | 1.32 |
| 16384 | mixed_both | xor | 998.90 | 853.86 | 1.17 | 1.11 |

Ratios above 1 favor AVX128. At 127–256 bits every tested case regressed against upstream. At 4,096–16,384 bits mixed-X/Z AND/OR and both-unknown XOR improved about 11–22%; known values remained slower. At 1,024 bits the small mixed-both AND/OR gains are not a robust adoption threshold. SIMD generally improves the custom scalar adapter for unknown values, but adapter overhead can erase that advantage against upstream.

Production arithmetic remains unchanged. These measurements do not establish a simulator-wide improvement, portable crossover or speedup for VeloSim W. The existing runtime was checked for correctness, not timed. No throughput claim is made for Windows or other CPUs.

Reproduce after configuring the existing optional slang v12.1 qualification build as Release with VELOSIM_COVERAGE=OFF and VELOSIM_SANITIZERS=OFF: build target `velosim_svint_simd_benchmark` with bounded Ninja workers, then execute `taskset -c <allowed-cpu> <build>/bin/velosim_svint_simd_benchmark` three times. Each invocation emits JSON including all seven raw timing samples per backend/case and the seed. Check all exit codes and status fields.

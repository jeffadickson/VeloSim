#pragma once
#include <stdint.h>
#if defined(_WIN32)
#define VS_API __declspec(dllexport)
#else
#define VS_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
#define VS_NOEXCEPT noexcept
extern "C" {
#else
#define VS_NOEXCEPT
#endif
VS_API uint32_t vs_ir_abi_version(void) VS_NOEXCEPT;
VS_API uint32_t vs_sched_abi_version(void) VS_NOEXCEPT;
VS_API uint64_t vs_ir_ticks(void*) VS_NOEXCEPT;
VS_API uint32_t vs_ir_build_profile(void) VS_NOEXCEPT;
VS_API void* vs_ir_new(void) VS_NOEXCEPT;
VS_API void vs_ir_free(void*) VS_NOEXCEPT;
VS_API int vs_ir_signal(void*,uint32_t,uint64_t,uint64_t,uint64_t) VS_NOEXCEPT;
VS_API int vs_ir_process(void*,uint32_t,uint32_t,uint32_t,uint32_t,uint8_t,uint8_t) VS_NOEXCEPT;
/* ABI2: op6/7/8/9 = AND/OR/XOR/NOT; op10/11/12 = unsigned
   add/subtract/multiply. Bitwise inputs resize unsigned to destination width.
   Arithmetic is modulo destination width; any operand X/Z yields all X. */
VS_API int vs_ir_process_v2(void* handle,uint32_t clock,uint32_t destination,uint32_t source_a,uint32_t source_b,uint32_t reset,uint8_t edge,uint8_t operation) VS_NOEXCEPT;
VS_API int vs_ir_read(void*,uint32_t,uint64_t*,uint64_t*,uint64_t*) VS_NOEXCEPT;
VS_API int vs_ir_drive(void*,const uint32_t*,const uint64_t*,const uint64_t*,const uint64_t*,uint32_t,uint32_t) VS_NOEXCEPT;
VS_API void* vs_sched_new(uint32_t) VS_NOEXCEPT;
VS_API void vs_sched_free(void*) VS_NOEXCEPT;
VS_API int vs_sched_get(void*,uint32_t) VS_NOEXCEPT;
VS_API uint64_t vs_sched_ticks(void*) VS_NOEXCEPT;
VS_API int vs_sched_add(void*,uint32_t,uint32_t,uint32_t,uint32_t,uint8_t,uint8_t) VS_NOEXCEPT;
VS_API int vs_sched_drive(void*,const uint32_t*,const uint8_t*,uint32_t,uint32_t) VS_NOEXCEPT;
#ifdef __cplusplus
}
#endif

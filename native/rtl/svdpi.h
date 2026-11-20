#pragma once
// IEEE 1800-2017 Annex I compatible SystemVerilog DPI-C core definitions for VeloSim.
#include "vpi_user.h"
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef uint8_t svScalar;
typedef svScalar svBit;
typedef svScalar svLogic;

#define sv_0 0
#define sv_1 1
#define sv_z 2
#define sv_x 3

typedef uint32_t svBitVecVal;
typedef struct t_vpi_vecval svLogicVecVal;

typedef void* svScope;

static inline svBit svGetBitselBit(const svBitVecVal* s, int i) {
 return (svBit)((s[i >> 5] >> (i & 31)) & 1u);
}

static inline void svPutBitselBit(svBitVecVal* d, int i, svBit s) {
 uint32_t mask = 1u << (i & 31);
 if (s & 1u) d[i >> 5] |= mask;
 else d[i >> 5] &= ~mask;
}

static inline svLogic svGetBitselLogic(const svLogicVecVal* s, int i) {
 uint32_t a = (s[i >> 5].aval >> (i & 31)) & 1u;
 uint32_t b = (s[i >> 5].bval >> (i & 31)) & 1u;
 return (svLogic)(a | (b << 1));
}

static inline void svPutBitselLogic(svLogicVecVal* d, int i, svLogic s) {
 uint32_t mask = 1u << (i & 31);
 if (s & 1u) d[i >> 5].aval |= mask; else d[i >> 5].aval &= ~mask;
 if (s & 2u) d[i >> 5].bval |= mask; else d[i >> 5].bval &= ~mask;
}

VS_VPI_API svScope svGetScope(void);
VS_VPI_API svScope svSetScope(const svScope scope);
VS_VPI_API const char* svGetNameFromScope(const svScope scope);
VS_VPI_API svScope svGetScopeFromName(const char* scopeName);

#ifdef __cplusplus
}
#endif

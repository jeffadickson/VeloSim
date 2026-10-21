/* VeloSim's implemented IEEE VPI subset. ABI-compatible 32-bit integer member.
 * Use a simulator's full standard vpi_user.h for additional declarations;
 * undeclared/unsupported services are not supplied by this runtime. */
#ifndef VPI_USER_H
#define VPI_USER_H
#include <stdint.h>
#if defined(_WIN32)
# if defined(VELOSIM_VPI_BUILD)
#  define VS_VPI_API __declspec(dllexport)
# else
#  define VS_VPI_API __declspec(dllimport)
# endif
#else
# define VS_VPI_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
extern "C" {
#endif
typedef int32_t PLI_INT32;
typedef uint32_t PLI_UINT32;
typedef char PLI_BYTE8;
typedef PLI_UINT32 *vpiHandle;
typedef struct t_vpi_time { PLI_INT32 type; PLI_UINT32 high, low; double real; } s_vpi_time, *p_vpi_time;
typedef struct t_vpi_vecval { PLI_UINT32 aval, bval; } s_vpi_vecval, *p_vpi_vecval;
struct t_vpi_strengthval;
typedef struct t_vpi_value {
 PLI_INT32 format;
 union { PLI_BYTE8 *str; PLI_INT32 scalar, integer; double real; p_vpi_time time; p_vpi_vecval vector; struct t_vpi_strengthval *strength; PLI_BYTE8 *misc; } value;
} s_vpi_value, *p_vpi_value;
typedef struct t_cb_data { PLI_INT32 reason; PLI_INT32 (*cb_rtn)(struct t_cb_data*); vpiHandle obj; p_vpi_time time; p_vpi_value value; PLI_INT32 index; PLI_BYTE8 *user_data; } s_cb_data, *p_cb_data;
typedef struct t_vpi_error_info { PLI_INT32 state, level; PLI_BYTE8 *message, *product, *code, *file; PLI_INT32 line; } s_vpi_error_info, *p_vpi_error_info;
typedef struct t_vpi_vlog_info { PLI_INT32 argc; PLI_BYTE8 **argv, *product, *version; } s_vpi_vlog_info, *p_vpi_vlog_info;
#define vpiIterator 27
#define vpiModule 32
#define vpiPort 44
#define vpiReg 48
#define vpiLowConn 80
#define vpiScope 84
#define vpiCallback 107
#define vpiType 1
#define vpiName 2
#define vpiFullName 3
#define vpiSize 4
#define vpiTopModule 7
#define vpiScalar 17
#define vpiVector 18
#define vpiSigned 65
#define vpiDefName 9
#define vpiDirection 20
#define vpiInput 1
#define vpiOutput 2
#define vpiNoDirection 5
#define vpiBinStrVal 1
#define vpiOctStrVal 2
#define vpiDecStrVal 3
#define vpiHexStrVal 4
#define vpiScalarVal 5
#define vpiIntVal 6
#define vpiVectorVal 9
#define vpiSuppressVal 13
#define vpiNoDelay 1
#define vpi0 0
#define vpi1 1
#define vpiZ 2
#define vpiX 3
#define vpiSimTime 2
#define vpiSuppressTime 3
#define vpiRun 3
#define vpiError 3
#define vpiFinish 67
#define cbValueChange 1
#define cbReadOnlySynch 7
#define cbStartOfSimulation 11
#define cbEndOfSimulation 12
VS_VPI_API vpiHandle vpi_handle_by_name(PLI_BYTE8*, vpiHandle);
VS_VPI_API vpiHandle vpi_handle(PLI_INT32, vpiHandle);
VS_VPI_API vpiHandle vpi_iterate(PLI_INT32, vpiHandle);
VS_VPI_API vpiHandle vpi_scan(vpiHandle);
VS_VPI_API PLI_INT32 vpi_free_object(vpiHandle);
VS_VPI_API PLI_INT32 vpi_compare_objects(vpiHandle, vpiHandle);
VS_VPI_API PLI_INT32 vpi_get(PLI_INT32, vpiHandle);
VS_VPI_API PLI_BYTE8 *vpi_get_str(PLI_INT32, vpiHandle);
VS_VPI_API void vpi_get_value(vpiHandle, p_vpi_value);
VS_VPI_API vpiHandle vpi_put_value(vpiHandle, p_vpi_value, p_vpi_time, PLI_INT32);
VS_VPI_API void vpi_get_time(vpiHandle, p_vpi_time);
VS_VPI_API vpiHandle vpi_register_cb(p_cb_data);
VS_VPI_API PLI_INT32 vpi_remove_cb(vpiHandle);
VS_VPI_API PLI_INT32 vpi_chk_error(p_vpi_error_info);
VS_VPI_API PLI_INT32 vpi_control(PLI_INT32, ...);
VS_VPI_API PLI_INT32 vpi_printf(PLI_BYTE8*, ...);
VS_VPI_API PLI_INT32 vpi_get_vlog_info(p_vpi_vlog_info);
#ifdef __cplusplus
}
#endif
#endif



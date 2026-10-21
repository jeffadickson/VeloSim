/* Also compiled in tests with Icarus's independent standard VPI header. */
#include <vpi_user.h>
#include <stdio.h>
#include <stdlib.h>
static vpiHandle counter;
#ifndef VELOSIM_PLUGIN_TOP
#define VELOSIM_PLUGIN_TOP "external_counter"
#endif
static int changes,readbacks,expected;
static void require(int ok){if(!ok){fputs("VPI plugin check failed\n",stderr);exit(91);}}
static PLI_INT32 readonly(p_cb_data unused){
 (void)unused;s_vpi_value value={0};value.format=vpiIntVal;vpi_get_value(counter,&value);
 require(!vpi_chk_error(NULL)&&value.value.integer==expected);++readbacks;return 0;
}
static PLI_INT32 changed(p_cb_data data){
 require(data->value&&data->value->format==vpiIntVal);expected=data->value->value.integer;++changes;
 s_cb_data cb={0};s_vpi_time time={vpiSimTime,0,0,0};cb.reason=cbReadOnlySynch;cb.cb_rtn=readonly;cb.time=&time;
 require(vpi_register_cb(&cb)!=NULL);return 0;
}
static PLI_INT32 start(p_cb_data unused){
 (void)unused;vpiHandle input=vpi_handle_by_name((PLI_BYTE8*)VELOSIM_PLUGIN_TOP ".data",NULL);require(input!=NULL&&vpi_get(vpiSize,input)==64);
 s_vpi_value value={0};value.format=vpiVectorVal;vpi_get_value(input,&value);require(!vpi_chk_error(NULL));
 s_vpi_vecval copied[2]={value.value.vector[0],value.value.vector[1]};value.value.vector=copied;
 vpi_put_value(input,&value,NULL,vpiNoDelay);require(!vpi_chk_error(NULL));vpi_free_object(input);return 0;
}
static PLI_INT32 end(p_cb_data unused){(void)unused;require(changes>=3&&readbacks==changes);vpi_printf((PLI_BYTE8*)"VPI_PLUGIN_PASS changes=%d\n",changes);return 0;}
static void setup(void){
 counter=vpi_handle_by_name((PLI_BYTE8*)VELOSIM_PLUGIN_TOP ".q",NULL);require(counter!=NULL);
 s_cb_data cb={0};s_vpi_value value={0};s_vpi_time time={vpiSuppressTime,0,0,0};
 value.format=vpiIntVal;cb.reason=cbValueChange;cb.cb_rtn=changed;cb.obj=counter;cb.value=&value;cb.time=&time;require(vpi_register_cb(&cb)!=NULL);
 cb=(s_cb_data){0};cb.reason=cbStartOfSimulation;cb.cb_rtn=start;require(vpi_register_cb(&cb)!=NULL);
 cb.reason=cbEndOfSimulation;cb.cb_rtn=end;require(vpi_register_cb(&cb)!=NULL);
}
void (*vlog_startup_routines[])(void)={setup,NULL};

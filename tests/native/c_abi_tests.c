#include "velosim.h"
#include <stdio.h>
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "C ABI check failed at %d\n", __LINE__); return 1; } } while (0)
int main(void) {
  CHECK(vs_ir_abi_version()==1 && vs_sched_abi_version()==1);
  void* engine=vs_ir_new(); CHECK(engine);
  CHECK(vs_ir_ticks(engine)==0);
  CHECK(vs_ir_drive(engine,NULL,NULL,NULL,NULL,0,1)==0);
  CHECK(vs_ir_ticks(engine)==1);
  CHECK(vs_ir_drive(engine,NULL,NULL,NULL,NULL,0,0)==-1);
  CHECK(vs_ir_ticks(engine)==1);
  vs_ir_free(engine);
  void* scheduler=vs_sched_new(1); CHECK(scheduler);
  CHECK(vs_sched_drive(scheduler,NULL,NULL,0,1)==0);
  CHECK(vs_sched_ticks(scheduler)==1);
  vs_sched_free(scheduler);
  return 0;
}

"""Validated VeloSim IR and ctypes interface to experimental native engine."""
from __future__ import annotations
import ctypes as C
import json
from pathlib import Path
import subprocess

class IRValidationError(ValueError): pass
OPCODES={'copy':0,'increment':1,'reset_increment':2,'sv_if_reset_increment':3}
EDGES={'posedge':0,'negedge':1}

def validate(ir):
    if not isinstance(ir,dict) or ir.get('format')!='velosim.ir.v1':
        raise IRValidationError('unsupported IR format')
    signals=ir.get('signals');procs=ir.get('processes')
    if not isinstance(signals,list) or not isinstance(procs,list) or not signals:
        raise IRValidationError('signals and processes must be nonempty lists')
    names={}
    for sig in signals:
        if not isinstance(sig,dict) or set(sig)-{'name','width','value','xmask','zmask'}:
            raise IRValidationError('invalid signal')
        name,w=sig.get('name'),sig.get('width')
        if not isinstance(name,str) or not name or name in names or type(w)!=int or not 1<=w<=64:
            raise IRValidationError('invalid signal name or width')
        vals=[sig.get(k,0) for k in ('value','xmask','zmask')]
        if any(type(v)!=int or not 0<=v<(1<<w) for v in vals) or vals[1]&vals[2]:
            raise IRValidationError('invalid four-state masks')
        names[name]=w
    destinations=set()
    for p in procs:
        if not isinstance(p,dict) or set(p)!={'clock','destination','source','reset','edge','operation'}:
            raise IRValidationError('invalid process')
        if any(p[k] not in names for k in ('clock','destination','source','reset')):
            raise IRValidationError('unknown signal')
        if names[p['clock']]!=1 or p['edge'] not in EDGES or p['operation'] not in OPCODES:
            raise IRValidationError('unsupported process')
        if p['destination'] in destinations:
            raise IRValidationError('multiple writers')
        destinations.add(p['destination'])
    return ir

def build_native_library(source,output):
    output=Path(output);output.parent.mkdir(parents=True,exist_ok=True)
    subprocess.run(['c++','-std=c++20','-O2','-shared','-fPIC','-pthread',str(source),'-o',str(output)],check=True)
    return output

class Simulation:
    def __init__(self,ir,library):
        self.ir=validate(ir);self.lib=C.CDLL(str(Path(library).resolve()))
        l=self.lib
        l.vs_ir_new.restype=C.c_void_p;l.vs_ir_free.argtypes=[C.c_void_p]
        l.vs_ir_signal.argtypes=[C.c_void_p,C.c_uint32,C.c_uint64,C.c_uint64,C.c_uint64];l.vs_ir_signal.restype=C.c_int
        l.vs_ir_process.argtypes=[C.c_void_p,C.c_uint32,C.c_uint32,C.c_uint32,C.c_uint32,C.c_uint8,C.c_uint8];l.vs_ir_process.restype=C.c_int
        l.vs_ir_read.argtypes=[C.c_void_p,C.c_uint32,C.POINTER(C.c_uint64),C.POINTER(C.c_uint64),C.POINTER(C.c_uint64)];l.vs_ir_read.restype=C.c_int
        l.vs_ir_drive.argtypes=[C.c_void_p,C.POINTER(C.c_uint32),C.POINTER(C.c_uint64),C.POINTER(C.c_uint64),C.POINTER(C.c_uint64),C.c_uint32,C.c_uint32];l.vs_ir_drive.restype=C.c_int
        self.handle=l.vs_ir_new()
        if not self.handle: raise RuntimeError('allocation failed')
        self.ids={}
        try:
            for sig in ir['signals']:
                idx=l.vs_ir_signal(self.handle,sig['width'],sig.get('value',0),sig.get('xmask',0),sig.get('zmask',0))
                if idx<0: raise RuntimeError('signal allocation failed')
                self.ids[sig['name']]=idx
            for p in ir['processes']:
                ret=l.vs_ir_process(self.handle,self.ids[p['clock']],self.ids[p['destination']],self.ids[p['source']],self.ids[p['reset']],EDGES[p['edge']],OPCODES[p['operation']])
                if ret: raise RuntimeError('process registration failed: '+str(ret))
        except BaseException:
            self.close();raise
    def close(self):
        if getattr(self,'handle',None):
            self.lib.vs_ir_free(self.handle);self.handle=None
    def __enter__(self):return self
    def __exit__(self,*_):self.close()
    def read(self,name):
        v,x,z=C.c_uint64(),C.c_uint64(),C.c_uint64()
        width=self.lib.vs_ir_read(self.handle,self.ids[name],C.byref(v),C.byref(x),C.byref(z))
        if width<0:raise RuntimeError('native read failed')
        return width,v.value,x.value,z.value
    def drive(self,updates,threads=1):
        if any(name not in self.ids for name in updates):raise KeyError('unknown signal')
        n=len(updates)
        ids=(C.c_uint32*n)(*(self.ids[k] for k in updates))
        vals=(C.c_uint64*n)(*(v[0] for v in updates.values()))
        xs=(C.c_uint64*n)(*(v[1] for v in updates.values()))
        zs=(C.c_uint64*n)(*(v[2] for v in updates.values()))
        ret=self.lib.vs_ir_drive(self.handle,ids,vals,xs,zs,n,threads)
        if ret<0:raise RuntimeError('native drive failed: '+str(ret))
        return ret

def main():
    import argparse
    p=argparse.ArgumentParser()
    p.add_argument('ir',type=Path);p.add_argument('--library',type=Path,required=True)
    p.add_argument('--threads',type=int,default=1);p.add_argument('--cycles',type=int,default=5)
    p.add_argument('--release-reset',action='store_true')
    a=p.parse_args()
    with Simulation(json.loads(a.ir.read_text()),a.library) as sim:
        for i in range(a.cycles):
            if i==1 and a.release_reset and 'rst_n' in sim.ids:sim.drive({'rst_n':(1,0,0)},a.threads)
            sim.drive({'clk':(0,0,0)},a.threads);sim.drive({'clk':(1,0,0)},a.threads)
            print('cycle',i+1,' '.join(f'{s["name"]}={sim.read(s["name"])}' for s in sim.ir['signals'] if s['name']!='clk'))
if __name__=='__main__':main()

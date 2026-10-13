"""Strict lowering of normalized UHDM-shaped trees; not raw Surelog binary UHDM."""
import json
from pathlib import Path
class UnsupportedUHDM(ValueError): pass
def require(test,msg):
    if not test:raise UnsupportedUHDM(msg)
def ref(x,name):return x=={'kind':'ref','name':name}
def const(x,n):return x=={'kind':'const','value':n}
def lower(data):
    require(isinstance(data,dict) and data.get('format')=='velosim.uhdm.normalized.v1','unsupported normalized UHDM')
    require(set(data)=={'format','signals','processes'},'unexpected UHDM fields')
    sigs=data['signals'];require(isinstance(sigs,list) and sigs,'missing signals')
    names={}
    for s in sigs:
        require(isinstance(s,dict) and set(s)=={'name','width'},'invalid signal')
        n,w=s['name'],s['width']
        require(isinstance(n,str) and n and n not in names,'duplicate signal')
        require(type(w)==int and 1<=w<=64,'unsupported width')
        names[n]=w
    procs=data['processes'];require(isinstance(procs,list) and procs,'missing processes')
    result=[]
    for p in procs:
        require(p.get('kind')=='always_ff','only always_ff supported')
        e=p.get('event',{});clk=e.get('signal')
        require(e.get('edge') in ('posedge','negedge') and names.get(clk)==1,'unsupported event')
        b=p.get('body',{});require(b.get('kind')=='if','expected if statement')
        c=b.get('condition',{});reset=c.get('operand',{}).get('name')
        require(c.get('kind')=='not' and ref(c.get('operand'),reset) and names.get(reset)==1,'invalid active-low reset')
        yes,no=b.get('then',{}),b.get('else',{})
        require(yes.get('kind')=='nba' and no.get('kind')=='nba','nonblocking assignments required')
        dst=yes.get('target')
        require(dst==no.get('target') and dst in names,'branch destinations differ')
        require(const(yes.get('value'),0),'zero reset required')
        inc=no.get('value',{})
        require(inc.get('kind')=='add' and ref(inc.get('lhs'),dst) and const(inc.get('rhs'),1),'only count + 1 supported')
        result.append({'clock':clk,'destination':dst,'source':dst,'reset':reset,'edge':e['edge'],'operation':'sv_if_reset_increment'})
    require(len({p['destination'] for p in result})==len(result),'multiple writers')
    ir={'format':'velosim.ir.v1','signals':[{'name':n,'width':w,'value':0} for n,w in names.items()],'processes':result}
    from .native_ir import validate
    return validate(ir)
def main():
    import argparse
    p=argparse.ArgumentParser();p.add_argument('normalized',type=Path);p.add_argument('-o','--output',type=Path,required=True)
    a=p.parse_args();ir=lower(json.loads(a.normalized.read_text()))
    a.output.parent.mkdir(parents=True,exist_ok=True)
    a.output.write_text(json.dumps(ir,indent=2)+'\n')
if __name__=='__main__':main()

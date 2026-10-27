// Host-driven example: op0 allocates fresh storage, op1 preserves its prefix,
// op2 deletes all cells and op3 writes one cell. Counts are bounded to256 by
// VeloSim's execution contract; this is a resource limit, not an SV type bound.
module dut(
 input logic clk,
 input logic [2:0] op,
 input logic [8:0] n,i,
 input logic [7:0] a,
 output logic [7:0] q,
 output int sz
);
 logic [7:0] m[];
 initial begin m=new[2];m[0]=1;end
 always @(posedge clk) begin
  case(op)
   0:m=new[n];
   1:m=new[n](m);
   2:m.delete();
   3:m[i]=a;
   default:begin end
  endcase
  q<=m[i];
  sz<=m.size();
 end
endmodule

// Explicit time metadata is preserved by the qualified Surelog overlay.
module timed_counter(output logic [7:0] count, delayed);
  timeunit 1ns;
  timeprecision 1ns;
  logic clk;
  initial begin
    clk = 0;
    count = 0;
    delayed = 0;
  end
  always #2 clk = ~clk;
  always @(posedge clk) begin
    count <= count + 1;
    delayed <= #1 count; // Sample now; commit one tick later.
  end
endmodule

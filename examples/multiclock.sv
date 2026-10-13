module multiclock(input clk_a, input clk_b, input d, output logic q0, output logic q1);
  always @(posedge clk_a) q0 <= d;
  always @(posedge clk_b) q1 <= q0;
endmodule

module external_counter(
 input logic clk, rst_n, enable,
 input logic [63:0] data,
 output logic [7:0] q,
 output logic [63:0] sampled, mirrored
);
 assign mirrored = data;
 always_ff @(posedge clk) begin
   if (!rst_n) begin q <= 8'b0; sampled <= 64'b0; end
   else if (enable) begin q <= q + 8'b1; sampled <= data; end
 end
endmodule

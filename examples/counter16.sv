module counter16(input logic clk, rst_n, output logic [15:0] count);
  always_ff @(posedge clk) begin
    if (!rst_n) count <= '0;
    else count <= count + 1'b1;
  end
endmodule

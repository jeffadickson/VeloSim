// Standalone HDL reference fixture for differential comparison.
// Native VeloSim executes an equivalent hand-authored IR, not this source.
module counter_tb;
  reg clk = 0;
  reg rst_n = 0;
  reg [7:0] count = 0;
  integer fd, status, reset_bit, cycle;
  always @(posedge clk) begin
    if (!rst_n) count <= 8'd0;
    else count <= count + 8'd1;
  end
  initial begin
    fd = $fopen("stimulus.txt", "r");
    if (!fd) $fatal(1, "stimulus.txt missing");
    cycle=0;
    while (!$feof(fd)) begin
      status = $fscanf(fd, "%d\n", reset_bit);
      if (status != 1) begin
        if (!$feof(fd)) $fatal(1, "malformed stimulus");
      end else begin
        rst_n = reset_bit[0];
        #1 clk = 1;
        #1 $display("VS_RESULT %0d %08b", cycle, count);
        #1 clk = 0;
        cycle = cycle + 1;
      end
    end
    $fclose(fd);
    $finish;
  end
endmodule

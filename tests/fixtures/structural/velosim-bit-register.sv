// Deliberately two-state, single-clock source used to pin the VeloSim RTL
// producer boundary. The bridge must not generalize this fixture to logic/X/Z
// semantics or a broader SystemVerilog subset.
module velosim_bit_register(
    input bit clk,
    input bit [7:0] data,
    output bit [7:0] q
);
    always_ff @(posedge clk) q <= data;
endmodule

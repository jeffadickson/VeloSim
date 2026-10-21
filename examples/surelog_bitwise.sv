module surelog_bitwise (
    input logic clk,
    input logic [7:0] a,
    input logic [15:0] b,
    input logic [63:0] wide,
    output logic [15:0] q_and, q_or, q_xor, q_not,
    output logic [16:0] q_nested,
    output logic [63:0] q_wide
);
    always_ff @(posedge clk) begin
        q_and <= a & b;
        q_or <= a | b;
        q_xor <= a ^ b;
        q_not <= ~a;
        q_nested <= ~(a + 8'd1) ^ b;
        q_wide <= ~wide ^ a;
    end
endmodule

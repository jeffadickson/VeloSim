// Bounded single-clock selection and concatenation example.
module surelog_selections (
    input logic clk,
    input logic [63:0] data,
    input logic [6:0] index,
    output logic selected,
    output logic [7:0] low_byte,
    output logic [7:0] high_byte,
    output logic [15:0] reordered,
    output logic [31:0] repeated
);
always_ff @(posedge clk) begin
    selected <= data[index];
    low_byte <= data[7:0];
    high_byte <= data[63:56];
    reordered <= {data[3:0], data[11:8], data[7:4], data[15:12]};
    repeated <= {4{data[7:0]}};
end
endmodule

// Same FIFO contract, using newly supported operators on binary controls and known pointers.
module surelog_fifo_ops #(parameter int WIDTH=8, DEPTH=4)(
 input logic clk, rst_n, push, pop,
 input logic [WIDTH-1:0] din,
 output logic [WIDTH-1:0] dout,
 output logic full, empty
);
 localparam int PTR_W = $clog2(DEPTH);
 logic [WIDTH-1:0] mem [DEPTH];
 logic [PTR_W-1:0] rd_ptr, wr_ptr;
 logic [$clog2(DEPTH+1)-1:0] count;
 assign full = (count >= DEPTH);
 assign empty = (count <= 0);
 assign dout = mem[rd_ptr];
 always_ff @(posedge clk) begin
   if (rst_n === 1'b0) begin rd_ptr <= '0; wr_ptr <= '0; count <= '0; end
   else begin
     if ((push || 1'b0) && !full) begin
       mem[wr_ptr] <= din;
       wr_ptr <= (wr_ptr / 1'b1) + 1'b1;
     end
     if (pop && !empty) rd_ptr <= pop ? ((rd_ptr * 1'b1) + 1'b1) : rd_ptr;
     case ({push && !full,pop && !empty})
       2'b10: count <= (count % 32'hffffffff) + 1'b1;
       2'b01: count <= count - 1'b1;
       default: count <= count;
     endcase
   end
 end
endmodule

// Ordered push-then-pop behavior; the queue starts empty and reset clears it.
module queue_demo(
    input logic clk, reset, push, pop, front,
    input logic [7:0] data_in,
    output logic [7:0] data_out,
    output int count
);
    logic [7:0] pending[$];
    always @(posedge clk) begin
        if (reset) begin
            pending.delete();
            data_out = 0;
        end else begin
            // The 256-cell simulator ceiling is a resource guard.
            if (push && pending.size() < 256) begin
                if (front) pending.push_front(data_in);
                else pending.push_back(data_in);
            end
            if (pop) begin
                if (front) data_out = pending.pop_front();
                else data_out = pending.pop_back();
            end
        end
        count <= pending.size();
    end
endmodule

// Single-cycle RV32I RISC-V processor core (IEEE 1800-2023 SystemVerilog).
// Supports LUI, AUIPC, JAL, JALR, BEQ/BNE/BLT/BGE/BLTU/BGEU, LW, SW,
// OP-IMM (ADDI/SLTI/SLTIU/XORI/ORI/ANDI/SLLI/SRLI/SRAI),
// OP (ADD/SUB/SLL/SLT/SLTU/XOR/SRL/SRA/OR/AND), and EBREAK/ECALL halt.
// Host loads machine code via imem_we/imem_waddr/imem_wdata while rst_n==0.
module rv32i_core (
    input  logic        clk,
    input  logic        rst_n,
    input  logic        imem_we,
    input  logic [7:0]  imem_waddr,
    input  logic [31:0] imem_wdata,
    input  logic [31:0] host_arg,
    output logic [31:0] pc,
    output logic [31:0] inst,
    output logic [31:0] x10_a0,
    output logic [31:0] x11_a1,
    output logic [31:0] cycle_count,
    output logic        halted
);
    logic [31:0] imem [0:255];
    logic [31:0] dmem [0:255];
    logic [31:0] regs [0:31];

    // Fetch
    assign inst = imem[pc[9:2]];

    // Decode fields
    logic [6:0]  opcode;
    logic [4:0]  rd, rs1, rs2;
    logic [2:0]  funct3;
    logic        funct7_5;
    assign opcode   = inst[6:0];
    assign rd       = inst[11:7];
    assign funct3   = inst[14:12];
    assign rs1      = inst[19:15];
    assign rs2      = inst[24:20];
    assign funct7_5 = inst[30];

    // Sign-extended immediates
    logic [31:0] imm_i, imm_s, imm_b, imm_u, imm_j;
    assign imm_i = {{20{inst[31]}}, inst[31:20]};
    assign imm_s = {{20{inst[31]}}, inst[31:25], inst[11:7]};
    assign imm_b = {{19{inst[31]}}, inst[31], inst[7], inst[30:25], inst[11:8], 1'b0};
    assign imm_u = {inst[31:12], 12'b0};
    assign imm_j = {{11{inst[31]}}, inst[31], inst[19:12], inst[20], inst[30:21], 1'b0};

    // Register file reads (x0 hardwired to 0; x12/a2 exposes host_arg when rd==0)
    logic [31:0] r1, r2;
    assign r1 = (rs1 == 5'd0) ? 32'd0 : regs[rs1];
    assign r2 = (rs2 == 5'd0) ? 32'd0 : regs[rs2];

    assign x10_a0 = regs[5'd10];
    assign x11_a1 = regs[5'd11];

    // ALU operands and arithmetic
    logic        is_op_imm, is_op, is_load, is_store, is_branch, is_jal, is_jalr, is_lui, is_auipc, is_sys;
    assign is_lui    = (opcode == 7'b0110111);
    assign is_auipc  = (opcode == 7'b0010111);
    assign is_jal    = (opcode == 7'b1101111);
    assign is_jalr   = (opcode == 7'b1100111);
    assign is_branch = (opcode == 7'b1100011);
    assign is_load   = (opcode == 7'b0000011);
    assign is_store  = (opcode == 7'b0100011);
    assign is_op_imm = (opcode == 7'b0010011);
    assign is_op     = (opcode == 7'b0110011);
    assign is_sys    = (opcode == 7'b1110011);

    logic [31:0] alu_b;
    assign alu_b = is_op ? r2 : imm_i;

    logic [4:0] shamt;
    assign shamt = alu_b[4:0];

    logic signed [31:0] r1_s, alu_b_s, r2_s;
    assign r1_s    = $signed(r1);
    assign alu_b_s = $signed(alu_b);
    assign r2_s    = $signed(r2);

    logic [31:0] alu_add_sub, alu_sra, alu_res;
    assign alu_add_sub = (is_op && funct7_5) ? (r1 - alu_b) : (r1 + alu_b);
    assign alu_sra     = $unsigned(r1_s >>> shamt);

    assign alu_res = (funct3 == 3'b000) ? alu_add_sub :
                     (funct3 == 3'b001) ? (r1 << shamt) :
                     (funct3 == 3'b010) ? ((r1_s < alu_b_s) ? 32'd1 : 32'd0) :
                     (funct3 == 3'b011) ? ((r1 < alu_b) ? 32'd1 : 32'd0) :
                     (funct3 == 3'b100) ? (r1 ^ alu_b) :
                     (funct3 == 3'b101) ? (funct7_5 ? alu_sra : (r1 >> shamt)) :
                     (funct3 == 3'b110) ? (r1 | alu_b) :
                                          (r1 & alu_b);

    // Memory address & load word (MMIO at 0xFFFF_FFF0 reads host_arg)
    logic [31:0] mem_addr, mem_rdata;
    assign mem_addr  = r1 + (is_store ? imm_s : imm_i);
    assign mem_rdata = (mem_addr == 32'hFFFF_FFF0) ? host_arg : dmem[mem_addr[9:2]];

    // Branch evaluation
    logic take_branch;
    assign take_branch = is_branch && (
        (funct3 == 3'b000) ? (r1 == r2) :
        (funct3 == 3'b001) ? (r1 != r2) :
        (funct3 == 3'b100) ? (r1_s < r2_s) :
        (funct3 == 3'b101) ? (r1_s >= r2_s) :
        (funct3 == 3'b110) ? (r1 < r2) :
        (funct3 == 3'b111) ? (r1 >= r2) : 1'b0
    );

    // Next PC & Writeback
    logic [31:0] pc_plus4, next_pc, wb_data;
    logic        wb_en;
    assign pc_plus4 = pc + 32'd4;
    assign next_pc  = is_jal      ? (pc + imm_j) :
                      is_jalr     ? ((r1 + imm_i) & 32'hFFFF_FFFE) :
                      take_branch ? (pc + imm_b) :
                                    pc_plus4;

    assign wb_en   = (rd != 5'd0) && (is_lui || is_auipc || is_jal || is_jalr || is_load || is_op_imm || is_op);
    assign wb_data = is_lui   ? imm_u :
                     is_auipc ? (pc + imm_u) :
                     (is_jal || is_jalr) ? pc_plus4 :
                     is_load  ? mem_rdata :
                                alu_res;

    always_ff @(posedge clk) begin
        if (imem_we) begin
            imem[imem_waddr] <= imem_wdata;
        end
        if (!rst_n) begin
            pc          <= 32'd0;
            cycle_count <= 32'd0;
            halted      <= 1'b0;
        end else if (!halted) begin
            cycle_count <= cycle_count + 32'd1;
            if (is_sys) begin
                halted <= 1'b1;
            end else begin
                pc <= next_pc;
                if (wb_en) begin
                    regs[rd] <= wb_data;
                end
                if (is_store) begin
                    dmem[mem_addr[9:2]] <= r2;
                end
            end
        end
    end
endmodule

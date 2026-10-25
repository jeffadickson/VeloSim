#pragma once
#include "runtime.h"
#include "vpi_user.h"
#include <memory>
#include <thread>
#include <utility>
#ifndef VS_VPI_API
#define VS_VPI_API
#endif
namespace vsrtl {
struct Signal { const char* name; int width, direction; bool is_signed=false; bool two_state=false; };
class VS_VPI_API Model {
 struct State;
 std::unique_ptr<State> state;
protected:
 std::vector<W> s;
 std::vector<std::vector<W>> mem;
 virtual void evaluate() = 0;
public:
 // clock is a scalar input index, or -1 for a clockless model. Initial evaluation is explicit.
 Model(std::string name, std::vector<Signal> signals, int clock,int word_abi=2);
 virtual ~Model();
 Model(const Model&) = delete; Model& operator=(const Model&) = delete;
 Model(Model&&) = delete; Model& operator=(Model&&) = delete;
 void eval(); void final(); void timeInc(uint64_t delta);
 uint64_t time() const; bool gotFinish() const; bool evalNeeded() const; bool started() const;
 void requestEval();
 void finish(); void useVpi(); void loadVpi(const std::string& path);
 const std::string& name() const; const std::vector<Signal>& signals() const;
 W get(size_t id) const; void put(size_t id, W value);
 std::weak_ptr<int> lifetime() const;
 void checkThread() const;
};
class Port {
 Model* model; size_t id;
public:
 Port(Model& owner, size_t index):model(&owner),id(index){}
 Port(const Port&) = delete; Port& operator=(const Port&) = delete;
 Port& operator=(uint64_t value){model->put(id,word(value,0,0,model->signals().at(id).width));return *this;}
 void bits(const std::string& value){if(value.size()!=size_t(model->signals().at(id).width))throw std::invalid_argument("port binary width mismatch");model->put(id,parse(value));}
 std::string bits() const{return text(model->get(id));}
 W value() const{return model->get(id);}
 explicit operator uint64_t() const{auto v=value();if(v.x||v.z)throw std::runtime_error("integer conversion of X/Z port");if(v.v>UINT64_MAX)throw std::overflow_error("port exceeds uint64_t");return uint64_t(v.v);}
};
}

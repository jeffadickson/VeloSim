#include "../native/uhdm/json_escape.h"
#include <cassert>
int main(){
 using velosim::json_escape;
 assert(json_escape("simple")=="\"simple\"");
 assert(json_escape("a\"b")=="\"a\\\"b\"");
 assert(json_escape("a\\b")=="\"a\\\\b\"");
 assert(json_escape("\n\t\r")=="\"\\n\\t\\r\"");
 assert(json_escape(std::string(1,'\x01'))=="\"\\u0001\"");
}

#pragma once
#include <string>
#include <string_view>
namespace velosim {
inline std::string json_escape(std::string_view text) {
  constexpr char digits[] = "0123456789abcdef";
  std::string out = "\"";
  for (unsigned char c : text) {
    switch (c) {
      case '"': out += "\\\""; break;
      case '\\': out += "\\\\"; break;
      case '\n': out += "\\n"; break;
      case '\r': out += "\\r"; break;
      case '\t': out += "\\t"; break;
      default:
        if (c < 0x20) { out += "\\u00"; out += digits[c >> 4]; out += digits[c & 15]; }
        else out += static_cast<char>(c);
    }
  }
  out += '"';
  return out;
}
}

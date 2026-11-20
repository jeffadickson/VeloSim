// Older pinned frontend models lose union tags; require an explicit corrected-writer capability.
bool union_tag_provenance=false;
// Packed type layout is derived from UHDM members, never from dotted source text.
// Count shared DAG edges as work: depth and width do not bound union fanout.
struct PackedTypeQuery {
 static inline unsigned active=0;
 static inline size_t remaining=0;
 static void step(){need(remaining>0,"packed type traversal budget exceeded");--remaining;}
 PackedTypeQuery(){if(!active)remaining=20000;step();++active;}
 ~PackedTypeQuery(){--active;}
 PackedTypeQuery(const PackedTypeQuery&)=delete;
 PackedTypeQuery& operator=(const PackedTypeQuery&)=delete;
};
int aggregate_width(H spec,int depth){
 PackedTypeQuery query;
 need(depth<32&&aggregate_spec(spec)&&vpi_get(vpiPacked,spec),"only bounded packed structs/unions supported");
 if(type(spec)==vpiUnionTypespec){need(union_tag_provenance,"union tag provenance requires patched frontend");need(!static_cast<const UHDM::union_typespec*>(identity(spec))->VpiTagged(),"tagged unions require tag-aware storage");}
 auto members=many(spec,vpiTypespecMember);need(!members.empty()&&members.size()<=256,"packed aggregate member budget exceeded");
 int total=0;std::set<std::string> names;
 for(auto member:members){PackedTypeQuery::step();need(names.insert(name(member)).second,"duplicate packed member");int w=width(member,depth+1);if(type(spec)==vpiUnionTypespec){need(total==0||total==w,"packed union members require equal widths");total=w;}else total+=w;need(total<=256,"packed aggregate exceeds256bits");}
 return total;
}
bool aggregate_state(H spec,int depth){
 PackedTypeQuery query;
 need(depth<32,"packed aggregate type depth exceeded");auto members=many(spec,vpiTypespecMember);need(!members.empty(),"packed aggregate requires members");std::optional<bool> state;
 for(auto member:members){PackedTypeQuery::step();auto child=word_spec(member);bool two=aggregate_spec(child)?aggregate_state(child,depth+1):two_state_word(member);need(!state||*state==two,"mixed two/four-state packed members unsupported");state=two;}return *state;
}
PackedField packed_field(H h){
 need(!inside_call,"packed subroutine member paths unsupported");auto object=static_cast<const UHDM::hier_path*>(identity(h));auto elements=object->Path_elems();need(elements&&elements->size()>=2&&elements->size()<=16,"packed field path requires2..16 elements");
 LoopHandles owned;auto root=owned.add(elements->front());auto declaration=one(root,vpiActual);need(declaration&&flat_symbols.count(identity(declaration)),"packed field root must bind a collected declaration");auto spec=word_spec(declaration);need(aggregate_spec(spec),"hierarchical non-aggregate paths unsupported");
 PackedField result{signal_name(root),0,width(declaration),false,false};
 for(size_t i=1;i<elements->size();++i){auto part=owned.add(elements->at(i));need(type(part)==vpiRefObj&&type(one(part,vpiActual))==vpiTypespecMember,"only whole packed member paths supported");auto members=many(spec,vpiTypespecMember);int offset=aggregate_width(spec,0);H selected=nullptr;
  for(auto member:members){int w=width(member);if(type(spec)!=vpiUnionTypespec)offset-=w;if(name(member)==name(part)){need(!selected,"ambiguous packed member");selected=member;result.offset+=type(spec)==vpiUnionTypespec?0:offset;result.width=w;}}
  need(selected,"member missing from bound packed type");auto actual=one(part,vpiActual);need(identity(actual)==identity(selected)||(!lexical_key(selected).empty()&&lexical_key(actual)==lexical_key(selected)&&width(actual)==width(selected)),"packed member must bind its typed declaration");result.sign=declared_signed(selected);result.two_state=two_state_word(selected);spec=word_spec(selected);if(i+1<elements->size())need(aggregate_spec(spec),"intermediate packed member is not an aggregate");
 }
 return result;
}
std::string packed_field_expression(H h,bool target){auto field=packed_field(h);auto selected="{\"kind\":\"partselect\",\"value\":{\"kind\":\"ref\",\"name\":"+q(field.root)+"},\"left\":"+std::to_string(field.offset+field.width-1)+",\"right\":"+std::to_string(field.offset)+"}";if(target)return selected;return "{\"kind\":\"convert\",\"value\":"+selected+",\"width\":"+std::to_string(field.width)+",\"signed\":"+(field.sign?"true":"false")+",\"two_state\":"+(field.two_state?"true":"false")+"}";}

pub fn patch_wpx_table_header(source: &str) -> Result<String, &'static str> {
    const CSTDDEF_INCLUDE: &str = "#include <cstddef>";
    const VECTOR_INCLUDE: &str = "#include <vector>";

    if source.contains(CSTDDEF_INCLUDE) {
        return Ok(source.to_string());
    }
    if !source.contains(VECTOR_INCLUDE) {
        return Err("WPXTable.h is missing its vector include anchor");
    }

    Ok(source.replacen(VECTOR_INCLUDE, "#include <cstddef>\n#include <vector>", 1))
}

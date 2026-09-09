#[path = "../build_wpd_patches.rs"]
mod build_wpd_patches;

use build_wpd_patches::patch_wpx_table_header;

#[test]
fn should_add_cstddef_before_vector_include() {
    let source = "#ifndef _WPXTABLE_H\n#define _WPXTABLE_H\n\n#include <vector>\n";

    let patched = patch_wpx_table_header(source).expect("patch WPXTable header");

    assert_eq!(
        patched,
        "#ifndef _WPXTABLE_H\n#define _WPXTABLE_H\n\n#include <cstddef>\n#include <vector>\n"
    );
}

#[test]
fn should_leave_patched_header_unchanged() {
    let source = "#include <cstddef>\n#include <vector>\n";

    assert_eq!(
        patch_wpx_table_header(source).expect("accept patched WPXTable header"),
        source
    );
}

#[test]
fn should_reject_header_without_vector_anchor() {
    let error = patch_wpx_table_header("#include <string>\n").expect_err("missing anchor must fail closed");

    assert_eq!(error, "WPXTable.h is missing its vector include anchor");
}

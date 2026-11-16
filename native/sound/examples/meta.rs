// meta <file>: object names of an audio .dat (scratch study)
use delirium_data::audioconfig::Metadata;
fn main() {
    let a: Vec<String> = std::env::args().collect();
    let raw = std::fs::read(&a[1]).unwrap();
    let m = Metadata::read(&raw).expect("parse");
    println!("version {} data {} strings {} objects {}", m.version, m.data.len(), m.strings.len(), m.objects.len());
    for s in &m.strings { println!("S {s}"); }
    for o in &m.objects { println!("O {} {} {} type {}", o.name, o.offset, o.size, m.data.get(o.offset as usize).copied().unwrap_or(0)); }
}

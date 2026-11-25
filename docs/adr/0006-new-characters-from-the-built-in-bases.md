# `new` starts from the built-in bases, not the sample models

`new` creates a character only from one of VRoid's two bases. The bases are
part of the install, so `new` works without network access and gives the same
character in every session, wearing only built-in items (R-EDIT-14).

## Considered options

- VRoid's sample models: rejected because VRoid downloads them on first use
  through a remote catalog that the pinned VRoid version does not fix, they
  come with their own terms of use, and their parts are custom items, which a
  template cannot carry to another character (R-EDIT-10). A sample model saved
  as a `.vroid` file can still be opened with `open`.
